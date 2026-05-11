<#
.SYNOPSIS
    Installs AiDaemon as a Windows Service running under a named user account.

.DESCRIPTION
    Runs `sc.exe create` with the exact argument syntax sc.exe demands (note the
    mandatory space after every `=`) and configures crash auto-restart so a transient
    failure doesn't take the daemon down for a whole day.

    Why a named user, not LocalSystem?
      * %USERPROFILE% under LocalSystem resolves to C:\Windows\System32\config\systemprofile,
        which breaks every `~/.claude/sessions/<PID>.json` read.
      * WMI Win32_Process cross-session lookups for child claude.exe processes are reliable
        only when the parent runs in the same desktop session.
      * Claude's auth and trust state live under the user profile and are per-account.

.PARAMETER BinDir
    Folder holding AiDaemon.exe and the appsettings files. Defaults to <repo>\publish.
    For production prefer something like C:\Tools\AiDaemon.

.PARAMETER ServiceUser
    Account the service runs as. Use ".\Jon" for a local account, "DOMAIN\Jon" for a
    domain account. The account must have "Log on as a service" rights -- grant via
    secpol.msc -> Local Policies -> User Rights Assignment, or
    `ntrights -u <user> +r SeServiceLogonRight`.

.PARAMETER ServiceName
    Defaults to AiDaemon.

.EXAMPLE
    .\scripts\install.ps1 -BinDir C:\Tools\AiDaemon -ServiceUser .\Jon
    # prompts for the password securely (Get-Credential)

.NOTES
    Must run elevated (sc.exe create needs Administrator).

    This script is ASCII-only by design. Windows PowerShell 5.1 reads .ps1 files in the
    OEM codepage unless a BOM is present, which mojibakes any non-ASCII character and
    triggers a parser error. Don't introduce em-dashes / smart quotes / arrows here.
#>
[CmdletBinding()]
param(
    [string] $BinDir = (Join-Path $PSScriptRoot '..\publish'),
    [Parameter(Mandatory = $true)]
    [string] $ServiceUser,
    [string] $ServiceName = 'AiDaemon'
)

$ErrorActionPreference = 'Stop'

# Refuse to run non-elevated rather than letting sc.exe emit a cryptic "Access is denied".
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "install.ps1 must run elevated (Administrator)."
}

$BinDir = [System.IO.Path]::GetFullPath($BinDir)
$exePath = Join-Path $BinDir 'AiDaemon.exe'
if (-not (Test-Path $exePath)) {
    throw "AiDaemon.exe not found at $exePath -- run .\scripts\publish.ps1 first."
}

# sc.exe obj= demands a qualified principal. ".\Jon", "COMPUTERNAME\Jon", "DOMAIN\Jon",
# and "Jon@example.com" all work for sc.exe, but the Get-Credential dialog on Win10/11
# with Microsoft-account sign-in REJECTS ".\Jon" (the tooltip says it only accepts
# DOMAIN\user or user@domain). Use $env:COMPUTERNAME -- accepted by both surfaces.
if ($ServiceUser -notmatch '[\\@]') {
    $qualified = "$env:COMPUTERNAME\$ServiceUser"
    Write-Host "ServiceUser '$ServiceUser' is unqualified -- using $qualified" -ForegroundColor Yellow
    $ServiceUser = $qualified
}
elseif ($ServiceUser -like '.\*') {
    # Local-prefix form works for sc.exe but Get-Credential rejects it on some SKUs.
    # Rewrite to the computer-qualified form for consistent behaviour across surfaces.
    $bare = $ServiceUser.Substring(2)
    $qualified = "$env:COMPUTERNAME\$bare"
    Write-Host "ServiceUser '$ServiceUser' rewritten to $qualified (Get-Credential rejects .\ shorthand)" -ForegroundColor Yellow
    $ServiceUser = $qualified
}

# Prompt for the service account password without echoing it.
$cred = Get-Credential -UserName $ServiceUser -Message "Enter password for service account $ServiceUser"
$plainPassword = $cred.GetNetworkCredential().Password

# Refuse to silently overwrite an existing service -- operator should explicitly uninstall first.
$existing = & sc.exe query $ServiceName 2>&1
if ($LASTEXITCODE -eq 0) {
    throw "Service '$ServiceName' already exists. Run .\scripts\uninstall.ps1 first, then re-run this."
}

Write-Host "Installing service '$ServiceName' from $exePath running as $ServiceUser" -ForegroundColor Cyan

# sc.exe requires a literal space after every `=` (e.g. "binPath= ..." not "binPath=..."),
# so the option name and value have to be SEPARATE argv slots. Passing them as a single
# array to Start-Process keeps PowerShell from collapsing the spaces and lets us
# interpolate $exePath / $ServiceUser / $plainPassword without command echo leaking them.
$createArgs = @(
    'create', $ServiceName,
    'binPath=', $exePath,
    'start=', 'auto',
    'obj=', $ServiceUser,
    'password=', $plainPassword,
    'DisplayName=', 'AiDaemon'
)
$proc = Start-Process -FilePath 'sc.exe' -ArgumentList $createArgs -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "sc.exe create failed with exit $($proc.ExitCode)"
}

Write-Host "Service created." -ForegroundColor Green

# Grant "Log on as a service" (SeServiceLogonRight). sc.exe is SUPPOSED to grant this
# automatically when you pass password=, but it silently skips on some Win10/11 SKUs
# (notably machines configured with Microsoft-account sign-in). Skipping the grant
# means the service installs fine and then fails to start with the cryptic
# "Error 1069: The service did not start due to a logon failure." -- which is
# indistinguishable from a wrong password without further digging.
#
# Idempotent: secedit /configure merges the SID into the existing right list, so
# re-running on a machine where the right is already present is a no-op.
$bareUser = if ($ServiceUser -match '\\') { $ServiceUser.Split('\\')[-1] } else { $ServiceUser.Split('@')[0] }
try {
    $sid = (New-Object System.Security.Principal.NTAccount($bareUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    Write-Host "Granting SeServiceLogonRight to $ServiceUser (SID $sid)..." -ForegroundColor Cyan

    $infFile = [IO.Path]::ChangeExtension([IO.Path]::GetTempFileName(), '.inf')
    $sdbFile = [IO.Path]::ChangeExtension([IO.Path]::GetTempFileName(), '.sdb')
    $infBody = @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeServiceLogonRight = *$sid
"@
    # secedit demands a UTF-16LE-with-BOM .inf or it silently produces "Task is completed"
    # while doing nothing. PowerShell 5.1's -Encoding Unicode emits exactly that.
    Set-Content -Path $infFile -Value $infBody -Encoding Unicode
    $secedit = Start-Process -FilePath 'secedit.exe' `
        -ArgumentList @('/configure', '/db', $sdbFile, '/cfg', $infFile, '/areas', 'USER_RIGHTS', '/quiet') `
        -NoNewWindow -Wait -PassThru
    Remove-Item $infFile, $sdbFile -Force -ErrorAction SilentlyContinue
    if ($secedit.ExitCode -ne 0) {
        Write-Warning "secedit returned exit $($secedit.ExitCode) -- if Start-Service fails with 1069, grant the right manually via secpol.msc."
    } else {
        Write-Host "SeServiceLogonRight granted." -ForegroundColor Green
    }
}
catch {
    Write-Warning "Could not resolve SID for '$bareUser': $($_.Exception.Message). If Start-Service fails with 1069, grant 'Log on as a service' manually via secpol.msc."
}

# Crash auto-restart: 5s, 5s, 30s, reset failure count after a successful 24h.
Write-Host "Configuring failure actions (restart on crash)..." -ForegroundColor Cyan
$failureArgs = @(
    'failure', $ServiceName,
    'reset=', '86400',
    'actions=', 'restart/5000/restart/5000/restart/30000'
)
$proc = Start-Process -FilePath 'sc.exe' -ArgumentList $failureArgs -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Warning "sc.exe failure returned exit $($proc.ExitCode) -- service is installed but auto-restart isn't configured."
}

# So `sc.exe qdescription AiDaemon 4096` shows operators what this is.
& sc.exe description $ServiceName 'Polls GitHub notifications scoped to the AI account, triages them, and spawns a Remote Control claude session per actionable event.' | Out-Null

Write-Host ""
Write-Host "Done. To start the service:" -ForegroundColor Green
Write-Host "  Start-Service $ServiceName"
Write-Host ""
Write-Host "Logs land under C:\ProgramData\AiDaemon\logs\ (or the DataDir configured in appsettings)." -ForegroundColor Yellow
Write-Host "Pause without uninstalling: New-Item C:\ProgramData\AiDaemon\PAUSED -ItemType File"

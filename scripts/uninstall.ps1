<#
.SYNOPSIS
    Stops and removes the AiDaemon Windows Service. Leaves files and state DB intact.

.DESCRIPTION
    The service registration is the only thing this script removes. Binaries under
    -BinDir and state under C:\ProgramData\AiDaemon\ are NOT deleted, so a re-install
    keeps the SQLite branches table (preserving session_id continuity) and the log
    history. Delete those manually if you want a true factory reset.

.PARAMETER ServiceName
    Defaults to AiDaemon.

.EXAMPLE
    .\scripts\uninstall.ps1

.NOTES
    Must run elevated.
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'AiDaemon'
)

$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "uninstall.ps1 must run elevated (Administrator)."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Write-Host "Service '$ServiceName' is not installed -- nothing to do." -ForegroundColor Yellow
    return
}

if ($svc.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    # Stop-Service returns as soon as the SCM acknowledges; wait for the process to
    # actually unwind so the subsequent delete doesn't race a lingering pid.
    $svc.WaitForStatus('Stopped', '00:00:30')
}

Write-Host "Deleting $ServiceName..." -ForegroundColor Cyan
& sc.exe delete $ServiceName

if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete failed with exit $LASTEXITCODE"
}

# sc.exe delete only MARKS the service for removal. The SCM finalises the deletion when
# every open handle on the service is closed -- a Services snap-in window (services.msc)
# or a sitting `sc.exe query` keeps the entry visible indefinitely. Poll briefly so the
# script doesn't claim success while the service is still hanging around.
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline) {
    if ($null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Host ""
        Write-Host "Service removed. Binaries and state DB are untouched." -ForegroundColor Green
        Write-Host "To wipe state too: Remove-Item C:\ProgramData\AiDaemon -Recurse -Force" -ForegroundColor Yellow
        return
    }
    Start-Sleep -Milliseconds 250
}

# Still visible after the poll window -- almost always because services.msc is open.
# Tell the operator exactly what to do; don't fail noisily because the deletion is
# already queued and will finalise the moment the offending handle goes away.
Write-Warning "Service '$ServiceName' is marked for deletion but still visible (SCM is holding the registration open)."
Write-Warning "Most likely cause: services.msc is open. Close every Services window, then run:"
Write-Warning "    Get-Service $ServiceName    # expect: nothing"
Write-Warning "If it persists, reboot -- the SCM rescans services at boot and finalises pending deletions."

$mmc = Get-Process mmc -ErrorAction SilentlyContinue
if ($mmc) {
    Write-Warning ("Found {0} mmc.exe process(es) running. Close any Services / Computer Management windows." -f $mmc.Count)
}

<#
.SYNOPSIS
    Installs AiDaemon as a per-user Scheduled Task that runs at logon.

.DESCRIPTION
    Why a Scheduled Task (not a Windows Service)?
      * Services run in session 0, which is non-interactive since Vista.
        AiDaemon spawns visible powershell + claude.exe console windows for
        every Remote Control session; under a service those windows live on
        a desktop nobody can see (and claude.exe can't read a real TTY there).
      * A scheduled task triggered "At log on" runs inside the user's
        interactive desktop session, so RC windows are visible and accept
        keyboard input.
      * "Run only when user is logged on" (LogonType Interactive) means we
        never store the user's password -- the task uses the live interactive
        token. No Get-Credential, no SeServiceLogonRight grant, no Error 1069.

    Crash auto-restart: up to 3 retries at 1-minute intervals. Task Scheduler
    refuses RestartInterval values below 1 minute, so we can't replicate the
    old service's 5s/5s/30s ladder; for AiDaemon's "poll every N seconds"
    cadence that's fine -- a 1-minute outage is still a single skipped tick.

.PARAMETER BinDir
    Folder holding AiDaemon.exe and the appsettings files. Defaults to <repo>\publish.
    For a stable install prefer something like C:\Tools\AiDaemon.

.PARAMETER TaskUser
    Account the task runs as, qualified as "COMPUTERNAME\User" or "DOMAIN\User".
    Defaults to the user invoking this script. Pass an explicit value only when
    installing on behalf of someone else; that case requires elevation and the
    target user must be logged on for the task to actually run.

.PARAMETER TaskName
    Defaults to AiDaemon.

.EXAMPLE
    .\scripts\install.ps1 -BinDir C:\Tools\AiDaemon
    # registers the task for the current user; runs at next logon

.NOTES
    Self-register for the current user does NOT require elevation. Registering
    for a different -TaskUser does, and Register-ScheduledTask will surface a
    clear access-denied if you try it from a non-elevated prompt.

    This script is ASCII-only by design. Windows PowerShell 5.1 reads .ps1
    files in the OEM codepage unless a BOM is present, which mojibakes any
    non-ASCII character and triggers a parser error. Don't introduce em-dashes
    / smart quotes / arrows here.
#>
[CmdletBinding()]
param(
    [string] $BinDir = (Join-Path $PSScriptRoot '..\publish'),
    [string] $TaskUser = "$env:USERDOMAIN\$env:USERNAME",
    [string] $TaskName = 'AiDaemon'
)

$ErrorActionPreference = 'Stop'

$BinDir = [System.IO.Path]::GetFullPath($BinDir)
$exePath = Join-Path $BinDir 'AiDaemon.exe'
if (-not (Test-Path $exePath)) {
    throw "AiDaemon.exe not found at $exePath -- run .\scripts\publish.ps1 first."
}

# Refuse to silently overwrite an existing registration -- operator should
# explicitly uninstall first, matching the old install.ps1's contract.
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Scheduled task '$TaskName' already exists. Run .\scripts\uninstall.ps1 first, then re-run this."
}

Write-Host "Registering scheduled task '$TaskName'" -ForegroundColor Cyan
Write-Host "  Action  : $exePath"
Write-Host "  WorkDir : $BinDir"
Write-Host "  Trigger : at logon of $TaskUser"

$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $BinDir

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $TaskUser

# LogonType Interactive keeps the task in the user's desktop session so
# spawned PowerShell + claude.exe windows are visible. S4U / Password /
# ServiceAccount all suppress the interactive desktop and defeat the whole
# reason we moved off the Windows Service. RunLevel Limited matches a normal
# user logon -- the daemon never needs elevation.
$principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType Interactive -RunLevel Limited

# Settings notes:
#   AllowStartIfOnBatteries / DontStopIfGoingOnBatteries: laptops can't have
#     the daemon disappear when you unplug. Both flags are required -- the
#     first allows initial start, the second keeps it running.
#   StartWhenAvailable: if the logon trigger is missed (e.g. machine was off
#     at the scheduled time, which doesn't apply to AtLogOn but is harmless),
#     run it at next opportunity.
#   RestartCount/RestartInterval: crash retry. 1-minute floor is enforced by
#     Task Scheduler -- don't bother trying smaller.
#   ExecutionTimeLimit (New-TimeSpan -Seconds 0): no time limit. Default is
#     3 days, which would silently kill the daemon mid-week.
#   MultipleInstances IgnoreNew: belt-and-braces against the AtLogOn trigger
#     firing twice during Fast User Switching or session reconnect.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Polls GitHub notifications scoped to the AI account, triages them, and spawns a Remote Control claude session per actionable event.' | Out-Null

Write-Host "Task registered." -ForegroundColor Green
Write-Host ""
Write-Host "To start the daemon now (without logging out and back in):" -ForegroundColor Green
Write-Host "  Start-ScheduledTask -TaskName $TaskName"
Write-Host ""
Write-Host "Logs land under C:\ProgramData\AiDaemon\logs\ (or the DataDir configured in appsettings)." -ForegroundColor Yellow
Write-Host "Pause without uninstalling: New-Item C:\ProgramData\AiDaemon\PAUSED -ItemType File"

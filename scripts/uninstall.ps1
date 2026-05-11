<#
.SYNOPSIS
    Stops and unregisters the AiDaemon scheduled task. Leaves files and state DB intact.

.DESCRIPTION
    The task registration is the only thing this script removes. Binaries under
    -BinDir and state under C:\ProgramData\AiDaemon\ are NOT deleted, so a
    re-install keeps the SQLite branches table (preserving session_id continuity)
    and the log history. Delete those manually if you want a true factory reset.

.PARAMETER TaskName
    Defaults to AiDaemon.

.EXAMPLE
    .\scripts\uninstall.ps1

.NOTES
    Self-uninstall for the current user does NOT require elevation. Removing a
    task registered for a different user does.
#>
[CmdletBinding()]
param(
    [string] $TaskName = 'AiDaemon'
)

$ErrorActionPreference = 'Stop'

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    Write-Host "Scheduled task '$TaskName' is not registered -- nothing to do." -ForegroundColor Yellow
    return
}

# Stop the running instance first so we don't leave an orphan AiDaemon.exe
# clutching the C:\ProgramData\AiDaemon\aidaemon.lock single-instance file.
if ($task.State -eq 'Running') {
    Write-Host "Stopping $TaskName..." -ForegroundColor Cyan
    Stop-ScheduledTask -TaskName $TaskName

    # Stop-ScheduledTask signals termination via the task host; the daemon's
    # graceful shutdown (releasing the lock, flushing Serilog, etc.) takes a
    # moment. Poll briefly so the unregister doesn't race a lingering handle.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $state = (Get-ScheduledTask -TaskName $TaskName).State
        if ($state -ne 'Running') { break }
        Start-Sleep -Milliseconds 250
    }

    if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') {
        Write-Warning "Task '$TaskName' is still running after 15s -- proceeding with unregister anyway; the orphan process should exit on its own."
    }
}

Write-Host "Unregistering $TaskName..." -ForegroundColor Cyan
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false

Write-Host ""
Write-Host "Task removed. Binaries and state DB are untouched." -ForegroundColor Green
Write-Host "To wipe state too: Remove-Item C:\ProgramData\AiDaemon -Recurse -Force" -ForegroundColor Yellow

# AiDaemon

A .NET 10 Windows Service that polls GitHub notifications scoped to an AI account, triages them with Claude, and spawns a Remote Control claude session per actionable event. The user drives every session — the daemon never edits, commits, or comments on its own.

See [plan.md](plan.md) for the full design.

## Quick start

```powershell
# 1. Local dev — run from VS Code or the CLI
dotnet run --project src\AiDaemon

# 2. Production — publish + install as a Windows Service
.\scripts\publish.ps1
.\scripts\install.ps1 -BinDir publish -ServiceUser .\Jon   # elevated; prompts for password
Start-Service AiDaemon
```

Before the first run, fill in `src\AiDaemon\appsettings.Local.json` with at least your ntfy topic (the file is gitignored):

```json
{
  "Daemon": {
    "AiUserLogin": "jon-or-ai",
    "WorktreeRoot": "D:\\git\\orez.worktrees",
    "RepoAllowlist": [ "ownerrez/orez" ],
    "Ntfy": { "Topic": "your-uuid-topic-name" }
  }
}
```

And confirm `gh auth status` reports the account matching `AiUserLogin` (run `gh auth login` if it doesn't — see [plan.md](plan.md) for the multi-account setup).

## Layout

- [src/AiDaemon/](src/AiDaemon/) — daemon source (worker host + services).
- [tests/AiDaemon.Tests/](tests/AiDaemon.Tests/) — xUnit + Moq unit tests.
- [scripts/](scripts/) — `publish.ps1`, `install.ps1`, `uninstall.ps1`.
- [plan.md](plan.md) — the design doc; phases, decisions, what's out of scope.

## Runbook — "I'm not getting pings"

### Where things live

| What | Path |
|---|---|
| Logs (rolling daily, 14 retained, 50MB cap each) | `C:\ProgramData\AiDaemon\logs\ai-daemon-<date>.log` |
| State DB (branches + dedup + rate limits) | `C:\ProgramData\AiDaemon\state.db` |
| Single-instance lock (also a PID dropbox) | `C:\ProgramData\AiDaemon\aidaemon.lock` |
| Pause flag — daemon skips polling while it exists | `C:\ProgramData\AiDaemon\PAUSED` |
| Per-worktree marker — never `claude --resume` against one of these | `<worktree>\.daemon-active` |
| Service-account home (where `~/.claude` lives) | `C:\Users\<ServiceUser>\.claude\` |

> All paths above assume the default `DataDir`. If you changed `DataDir` in `appsettings.json`, substitute accordingly.

### Triage by symptom

**1. The daemon isn't logging anything.**
```powershell
Get-Service AiDaemon                                                # Running?
Get-Content C:\ProgramData\AiDaemon\aidaemon.lock                   # who owns the lock
Get-ChildItem C:\ProgramData\AiDaemon\logs\ | Sort LastWriteTime -Descending | Select -First 1
```
If the service is stopped, `Start-Service AiDaemon`. If it's running but the log isn't updating, check Event Viewer → Windows Logs → System for `AiDaemon` crash entries — `sc.exe failure` will restart it but a crash loop has a 5/5/30s cadence visible in the log.

**2. Polling logs are running but no ntfy buzz.**
```powershell
# What does the daemon think is happening?
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "tick seen=" -Tail 20
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "verdict.*action=" -Tail 20
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "NtfyPusher|ntfy push" -Tail 20
```
If you see `dropped:` outcomes — the comment didn't match `ActionableReasons` or matched an `L2DropPatterns`. If you see `escalated`/`spawned:` but no `ntfy push returned`, the topic is unset or the body is wrong.

**2b. Service won't start — Error 1069 "logon failure".**
The account is missing the `SeServiceLogonRight` ("Log on as a service") privilege. `sc.exe` is supposed to grant this when `install.ps1` passes `password=`, but it silently skips on some Win10/11 SKUs (especially MSA-sign-in boxes). `install.ps1` now runs a `secedit /configure` pass to grant it explicitly; if you installed before that change, grant it manually:
```powershell
# elevated
$sid = (New-Object System.Security.Principal.NTAccount('Jon')).Translate([System.Security.Principal.SecurityIdentifier]).Value
$inf = "[Unicode]`nUnicode=yes`n[Version]`nsignature=`"`$CHICAGO`$`"`nRevision=1`n[Privilege Rights]`nSeServiceLogonRight = *$sid`n"
$f = [IO.Path]::ChangeExtension([IO.Path]::GetTempFileName(), '.inf')
Set-Content $f $inf -Encoding Unicode
secedit /configure /db ([IO.Path]::ChangeExtension([IO.Path]::GetTempFileName(),'.sdb')) /cfg $f /areas USER_RIGHTS
Start-Service AiDaemon
```
Or via UI: `secpol.msc` → Local Policies → User Rights Assignment → "Log on as a service" → Add User. If the password is wrong instead, `Start-Service` returns 1069 with the same message — distinguish by validating against the local SAM (`[System.DirectoryServices.AccountManagement.PrincipalContext]::new('Machine').ValidateCredentials('Jon', $pw)` returning `False` ⇒ password problem, not rights).

**3. Pings stopped after a token rotation.**
Auth health checks fire at startup; if they fail, the daemon pushes a high-priority alert (title `AiDaemon: gh not authenticated` or `AiDaemon: gh token invalid`). Without that buzz, the runtime probe still logs `Critical`. To recheck manually:
```powershell
gh auth status                                                       # local config
gh api /user                                                         # live token
```
Re-login if needed, then restart the service: `Restart-Service AiDaemon`.

**4. The phone topic looks dead.**
```powershell
# Fire a manual test buzz at the exact server+topic the daemon uses
$topic = (Get-Content src\AiDaemon\appsettings.Local.json | ConvertFrom-Json).Daemon.Ntfy.Topic
Invoke-RestMethod -Uri "https://ntfy.sh/$topic" -Method Post -Body "manual test"
```
If the phone buzzes, the daemon's config is wrong. If it doesn't, resubscribe to `<topic>` in the ntfy app — push tokens occasionally rotate.

**5. The daemon spawned an RC session but the URL doesn't open on the phone.**
```powershell
# What did the daemon record for that branch?
sqlite3 C:\ProgramData\AiDaemon\state.db "SELECT branch, mode, rc_url, rc_pid, rc_claude_pid FROM branches"
```
A stale `rc_url` is reaped by the 60s sweep when the process or bridge dies. Force a respawn by killing the row and posting a new comment, or wait for the next tick: the sweep handles it.

### Common toggles

| Action | How |
|---|---|
| Pause polling (keep daemon running) | `New-Item C:\ProgramData\AiDaemon\PAUSED -ItemType File -Force` |
| Resume polling | `Remove-Item C:\ProgramData\AiDaemon\PAUSED` |
| Rotate ntfy topic | Edit `appsettings.Local.json` → `Restart-Service AiDaemon` → resubscribe on phone |
| Inspect state DB | `sqlite3 C:\ProgramData\AiDaemon\state.db ".tables"` then `SELECT * FROM branches` |
| Tail today's log | `Get-Content C:\ProgramData\AiDaemon\logs\ai-daemon-$(Get-Date -Format yyyyMMdd).log -Wait` |
| Reseed a single kv entry | `AiDaemon.exe set-kv <key> <value>` (one-shot subcommand) |
| Wipe all state (nuclear) | `Stop-Service AiDaemon; Remove-Item C:\ProgramData\AiDaemon -Recurse; Start-Service AiDaemon` |

### Warnings

- **Never run `claude --resume <sid>` against a worktree containing `.daemon-active`** — this violates the single-writer invariant (two processes appending to the same JSONL ⇒ corrupted history). The marker file is the safety net; if you genuinely need to drive a session by hand, stop the service or wait for the idle-timeout sweep to reap it.
- **PATH for the service identity.** `claude` and `gh` need to be reachable from the service account's user PATH. The reliable workaround is to set absolute paths in `appsettings.json` (`"ClaudePath": "C:\\Users\\Jon\\AppData\\Roaming\\npm\\claude.cmd"`); the brittle one is to augment `HKLM\SYSTEM\CurrentControlSet\Services\AiDaemon\Environment` and reboot. The Windows Service Control Manager does not inherit user-PATH at start time.
- **Don't run the daemon as LocalSystem.** Under LocalSystem, `%USERPROFILE%` resolves to `C:\Windows\System32\config\systemprofile`, which breaks `~/.claude/sessions/<PID>.json` reads and WMI cross-session lookups. The install script defaults to a named user.

## Development

```powershell
dotnet build                                                         # builds src + tests
dotnet test                                                          # runs xUnit suite (~180 tests)
dotnet run --project src\AiDaemon                                    # foreground; Ctrl+C to stop
```

The `appsettings.Development.json` file overrides the poll interval to 10s and bumps Serilog to Debug — picked up automatically when running with `DOTNET_ENVIRONMENT=Development` or via `dotnet run`.

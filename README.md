# AiDaemon

A .NET 10 background worker that polls GitHub notifications scoped to an AI account, triages them with Claude, and spawns a Remote Control claude session per actionable event. The user drives every session — the daemon never edits, commits, or comments on its own.

Runs as a per-user **scheduled task** triggered at logon, not as a Windows Service: every RC session needs a visible PowerShell + claude.exe console window, and services live in session 0 (no interactive desktop). A scheduled task with `LogonType Interactive` runs inside the user's desktop session so RC windows are visible and accept keyboard input.

The daemon also surfaces a **system-tray icon** while it's running — right-click for:

- **Show today's log** — opens a tailing PowerShell window on `ai-daemon-<date>.log`
- **Open log folder** — Explorer to the log directory
- **Pause / Resume polling** — toggles the `PAUSED` flag file (no restart needed)
- **Retry ►** — submenu of the 20 most-recently-processed notifications. Selecting one re-fetches the thread from GitHub via `/notifications/threads/{id}`, deletes the dedup row, and runs the same L1/L2/L3 + dispatch pipeline against it. Rate-limit budget is not charged for retries; outcome is surfaced as a balloon notification.
- **Quit** — graceful host shutdown

See [plan.md](plan.md) for the full design.

## Quick start

```powershell
# 1. Local dev — run from VS Code or the CLI
dotnet run --project src\AiDaemon

# 2. Production — publish + register the scheduled task
.\scripts\publish.ps1
.\scripts\install.ps1 -BinDir publish              # registers for the current user, no password needed
Start-ScheduledTask -TaskName AiDaemon             # or just log out and back in
```

Before the first run, fill in `src\AiDaemon\appsettings.Local.json` with at least your ntfy topic (the file is gitignored):

```json
{
  "Daemon": {
    "AiUserLogin": "jon-or-ai",
    "WorktreeRoot": "D:\\git\\orez.worktrees",
    "RepoAllowlist": [ "ownerrez/orez" ],
    "RepoRoots": { "ownerrez/orez": "D:\\git\\orez" },
    "Ntfy": { "Topic": "your-uuid-topic-name" }
  }
}
```

And confirm `gh auth status` reports the account matching `AiUserLogin` (run `gh auth login` if it doesn't — see [plan.md](plan.md) for the multi-account setup).

`RepoRoots` maps each allowlisted repo to its main clone. When a notification arrives for a branch that has no matching worktree under `WorktreeRoot`, the daemon shells `git -C <RepoRoots[repo]> worktree add <WorktreeRoot>\<branch> <branch>` to materialize one. The branch must already exist as a local ref — cross-fork PRs and unfetched refs silently skip (re-run `git fetch` and the next poll will pick them up). For Issue notifications without a branch, the daemon looks up `refs/heads/<issue>-*` in the main clone and only auto-creates when there's exactly one unambiguous match. Omit `RepoRoots` (or an entry within it) to keep the legacy "skip if no worktree" behavior for that repo.

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
| Task-user home (where `~/.claude` lives) | `C:\Users\<TaskUser>\.claude\` |

> All paths above assume the default `DataDir`. If you changed `DataDir` in `appsettings.json`, substitute accordingly.

### Triage by symptom

**1. The daemon isn't logging anything.**
```powershell
Get-ScheduledTask -TaskName AiDaemon | Select TaskName, State        # Running / Ready / Disabled?
Get-Content C:\ProgramData\AiDaemon\aidaemon.lock                    # who owns the lock
Get-ChildItem C:\ProgramData\AiDaemon\logs\ | Sort LastWriteTime -Descending | Select -First 1
```
If the task is `Ready` (not running), kick it: `Start-ScheduledTask -TaskName AiDaemon`. If it's `Running` but the log isn't updating, check the last run result and history:
```powershell
Get-ScheduledTaskInfo -TaskName AiDaemon | Select LastRunTime, LastTaskResult, NumberOfMissedRuns
```
A non-zero `LastTaskResult` is the Win32 exit code of the most recent failure. Task Scheduler restarts the daemon up to 3 times at 1-minute intervals on crash; a tight loop is visible in the log.

**2. Polling logs are running but no ntfy buzz.**
```powershell
# What does the daemon think is happening?
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "tick seen=" -Tail 20
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "verdict.*action=" -Tail 20
Select-String -Path C:\ProgramData\AiDaemon\logs\*.log -Pattern "NtfyPusher|ntfy push" -Tail 20
```
If you see `dropped:` outcomes — the comment didn't match `ActionableReasons` or matched an `L2DropPatterns`. If you see `escalated`/`spawned:` but no `ntfy push returned`, the topic is unset or the body is wrong.

**2b. Task is registered but never runs.**
Check the principal's logon type and that the user is actually signed in:
```powershell
(Get-ScheduledTask -TaskName AiDaemon).Principal | Select UserId, LogonType, RunLevel
(Get-ScheduledTask -TaskName AiDaemon).Triggers  | Select TriggerType, UserId, Enabled
quser                                                                # is the task's UserId logged on?
```
Expected: `LogonType=Interactive`, `RunLevel=Limited`, an `AtLogOn` trigger keyed to your user, and that user present in `quser` output. `Interactive` means "run only when the user is logged on" — switching to `S4U` or `Password` would let the task run headless but would also strip the interactive desktop, defeating RC window visibility.

**3. Pings stopped after a token rotation.**
Auth health checks fire at startup; if they fail, the daemon pushes a high-priority alert (title `AiDaemon: gh not authenticated` or `AiDaemon: gh token invalid`). Without that buzz, the runtime probe still logs `Critical`. To recheck manually:
```powershell
gh auth status                                                       # local config
gh api /user                                                         # live token
```
Re-login if needed, then bounce the daemon: `Stop-ScheduledTask -TaskName AiDaemon; Start-ScheduledTask -TaskName AiDaemon`.

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
| Rotate ntfy topic | Edit `appsettings.Local.json` → `Stop-ScheduledTask AiDaemon; Start-ScheduledTask AiDaemon` → resubscribe on phone |
| Inspect state DB | `sqlite3 C:\ProgramData\AiDaemon\state.db ".tables"` then `SELECT * FROM branches` |
| Tail today's log | `Get-Content C:\ProgramData\AiDaemon\logs\ai-daemon-$(Get-Date -Format yyyyMMdd).log -Wait` |
| Reseed a single kv entry | `AiDaemon.exe set-kv <key> <value>` (one-shot subcommand) |
| Wipe all state (nuclear) | `Stop-ScheduledTask AiDaemon; Remove-Item C:\ProgramData\AiDaemon -Recurse; Start-ScheduledTask AiDaemon` |

### Warnings

- **Never run `claude --resume <sid>` against a worktree containing `.daemon-active`** — this violates the single-writer invariant (two processes appending to the same JSONL ⇒ corrupted history). The marker file is the safety net; if you genuinely need to drive a session by hand, stop the task (`Stop-ScheduledTask AiDaemon`) or wait for the idle-timeout sweep to reap it.
- **The task must run as a real user, not as `SYSTEM` or `LOCAL SERVICE`.** Those identities resolve `%USERPROFILE%` to `C:\Windows\System32\config\systemprofile`, which breaks `~/.claude/sessions/<PID>.json` reads. They also live in session 0, which voids the whole reason we picked a scheduled task. `install.ps1` defaults to the invoking user, which is what you want.
- **Logging out kills the daemon.** With `LogonType Interactive` the task stops when the user signs out. That's intentional — RC windows can't survive their parent desktop disappearing — but it does mean a deliberate sign-out pauses pings until next logon. To keep things polling while away from the desk, leave the session active (lock-screen is fine) rather than signing out.

## Development

```powershell
dotnet build                                                         # builds src + tests
dotnet test                                                          # runs xUnit suite (~180 tests)
dotnet run --project src\AiDaemon                                    # foreground; Ctrl+C to stop
```

The `appsettings.Development.json` file overrides the poll interval to 10s and bumps Serilog to Debug — picked up automatically when running with `DOTNET_ENVIRONMENT=Development` or via `dotnet run`.

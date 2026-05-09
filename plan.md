# Daemon implementation plan (C#)

A .NET 8 Worker Service that polls GitHub notifications scoped to the AI account, triages each to filter out noise, and spawns a Remote Control session per [recipe.md](recipe.md) for anything actionable. The user drives every session — the daemon never edits, commits, or comments on its own.

Worktrees are assumed to already exist (one per active branch, named `<issue>-<slug>`). The daemon does not create or destroy them.

## Stack

| Concern | Pick | Why |
|---|---|---|
| Host | .NET 10 Worker Service (`dotnet new worker`) | `AddWindowsService()` on `HostApplicationBuilder` gives proper service semantics in three lines |
| GitHub API (all of it) | Shell out to `gh` CLI | Auth, token refresh, rate limits, User-Agent — all handled. Daemon uses your global `gh auth` (no PAT plumbing, no separate config dir). |
| State | `Microsoft.Data.Sqlite` + `Dapper` | One file, ACID, three small tables |
| Logging | Serilog + file sink | Rolling daily log under `C:\ProgramData\AiDaemon\logs` |
| JSON | `System.Text.Json` | Built-in, fast, source-generated converters available |
| Tests | xUnit + Moq | Matches the OwnerRez project convention |
| Process spawning | `System.Diagnostics.Process` + `System.Management` (WMI) for parent/child lookup | Native Windows; matches the recipe's `Start-Process -WorkingDirectory` semantics |
| Config | `appsettings.json` (committed) + `appsettings.Local.json` (gitignored, holds ntfy topic) | Service-identity-safe, no `LoadUserProfile` plumbing |

## Project layout

```
daemon/
├── recipe.md                       (existing — RC spawn primitives)
├── plan.md                         (this file)
├── AiDaemon.sln
├── src/AiDaemon/
│   ├── AiDaemon.csproj
│   ├── Program.cs                  (host builder, DI registration)
│   ├── Worker.cs                   (BackgroundService — the loop)
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── Configuration/DaemonOptions.cs
│   ├── Models/
│   │   ├── BranchState.cs
│   │   ├── BranchMode.cs           (enum: Idle, RcActive)
│   │   ├── GhNotification.cs
│   │   └── TriageVerdict.cs        (action: Drop|Actionable)
│   ├── Services/
│   │   ├── INotificationPoller.cs  / NotificationPoller.cs
│   │   ├── ITriagePipeline.cs      / TriagePipeline.cs
│   │   ├── IBranchResolver.cs      / BranchResolver.cs
│   │   ├── IDispatcher.cs          / Dispatcher.cs
│   │   ├── IRcLauncher.cs          / RcLauncher.cs            (recipe.md primitives)
│   │   ├── IClaudeRunner.cs        / ClaudeRunner.cs          (claude -p invocations)
│   │   ├── IGhClient.cs            / GhClient.cs              (`gh` CLI shellouts via IProcessRunner)
│   │   └── INotificationPusher.cs  / NtfyPusher.cs
│   ├── Storage/
│   │   ├── IStateStore.cs          / SqliteStateStore.cs
│   │   └── Schema.sql              (embedded resource, applied on startup)
│   ├── Process/
│   │   ├── IProcessRunner.cs       / ProcessRunner.cs           (single subprocess wrapper used by gh, claude, git)
│   │   └── ProcessResult.cs        (record: ExitCode, Stdout, Stderr)
│   └── Io/
│       └── IFileSystem.cs          / FileSystem.cs              (thin wrapper for testable file I/O — registry polling, .daemon-active, JSONL mtime)
└── tests/AiDaemon.Tests/
    ├── AiDaemon.Tests.csproj
    └── …
```

## Configuration shape

`appsettings.json`:

```json
{
  "Daemon": {
    "PollIntervalSeconds": 60,
    "AiUserLogin": "jon-or-ai",
    "WorktreeRoot": "C:\\Users\\Jon\\worktrees",
    "ClaudePath": "claude",
    "PowerShellPath": "powershell.exe",
    "GhPath": "gh",
    "RepoAllowlist": [ "ownerrez/orez" ],
    "ActionableReasons": [ "mention", "review_requested", "team_mention", "assign", "comment", "author" ],
    "BotAuthorBlocklist": [ "dependabot[bot]", "renovate[bot]", "github-actions[bot]" ],
    "RcIdleTimeoutHours": 2,
    "Triage": {
      "Model": "haiku",
      "BareMode": true,
      "MaxActionsPerThreadPerDay": 5,
      "L2DropPatterns": [ "^\\s*(thanks|lgtm|approved|👍|:\\+1:)\\s*$" ]
    },
    "Ntfy": {
      "Server": "https://ntfy.sh",
      "PriorityNormal": 3,
      "PriorityHigh": 4
    }
  }
}
```

Local-only config in `appsettings.Local.json` (gitignored, sits next to `appsettings.json`):

```json
{
  "Daemon": {
    "Ntfy": {
      "Topic": "<UUID-like opaque topic name>"
    }
  }
}
```

`Program.cs` adds it after the base file: `builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)`. No environment-variable plumbing, no `LoadUserProfile` headache when running as a service — just a file the binary reads from its install directory. Add `appsettings.Local.json` to `.gitignore`.

GitHub auth uses the global `gh` CLI auth — whatever `gh auth status` reports for the user the daemon runs as. `AiUserLogin` in config must match that account's login (it powers L1 self-drop). One-time setup if not already authed:

```powershell
gh auth login   # pick GitHub.com, choose HTTPS, authenticate as the desired account
gh auth status  # confirm
```

If you want a dedicated AI identity (e.g. `jon-or-ai`) without taking over your interactive `gh`, log into that account via `gh auth login` and switch with `gh auth switch -u <login>` when you want to use it interactively as yourself. The daemon will use whichever account is currently active.

## Storage schema

```sql
CREATE TABLE IF NOT EXISTS branches (
  branch          TEXT PRIMARY KEY,           -- "owner/repo:412-fix-x"
  session_id      TEXT NOT NULL,              -- durable Claude UUID
  worktree        TEXT NOT NULL,
  mode            TEXT NOT NULL,              -- 'Idle' | 'RcActive'
  rc_pid          INTEGER,
  rc_claude_pid   INTEGER,
  rc_claude_start INTEGER,                    -- Process.StartTime ticks; defends against PID recycling
  rc_bridge_id    TEXT,
  rc_url          TEXT,
  last_event_at   INTEGER NOT NULL,
  pr_number       INTEGER,
  issue_number    INTEGER
);

CREATE TABLE IF NOT EXISTS processed (
  thread_id       TEXT NOT NULL,
  comment_id      TEXT,
  processed_at    INTEGER NOT NULL,
  outcome         TEXT NOT NULL,              -- 'dropped:<reason>' | 'escalated' | 'heads_up' | 'failed:<msg>'
  PRIMARY KEY (thread_id, comment_id)
);

CREATE TABLE IF NOT EXISTS rate_limits (
  thread_id       TEXT NOT NULL,
  day             TEXT NOT NULL,              -- YYYY-MM-DD UTC
  count           INTEGER NOT NULL,
  PRIMARY KEY (thread_id, day)
);
```

Database lives at `C:\ProgramData\AiDaemon\state.db` (service-identity-safe, not user-profile-dependent). Schema is embedded as a resource and applied with `CREATE TABLE IF NOT EXISTS` on startup.

## Phases

Six phases. Each is shippable, runnable, and testable on its own.

### Phase 0 — Bootstrap (~1 evening)

**Goal:** project compiles, runs as a console app, logs "tick" once a minute.

- [ ] `dotnet new worker -o src/AiDaemon` and `dotnet new sln`, add project, init git.
- [ ] Set TFM to `net10.0-windows` in `AiDaemon.csproj` (System.Management is Windows-only; avoids CA1416 noise).
- [ ] Add NuGet refs: Microsoft.Data.Sqlite, Dapper, Serilog.Extensions.Hosting, Serilog.Sinks.File, Serilog.Sinks.Console, Microsoft.Extensions.Http, System.Management.
- [ ] `Program.cs`: `Host.CreateApplicationBuilder(args)` + `builder.Services.AddWindowsService(o => o.ServiceName = "AiDaemon")` (the `.UseWindowsService()` extension only exists on `IHostBuilder`, not the newer `HostApplicationBuilder` — common trap) + `Configure<DaemonOptions>(...)` + Serilog.
- [ ] `IProcessRunner` interface and `ProcessRunner` impl up front. Every subprocess in this codebase (`gh`, `claude`, `git`, `powershell`) goes through it. Wraps `Process.Start` with: `UseShellExecute=false`, `RedirectStandardOutput/Error/Input`, `StandardOutputEncoding=Encoding.UTF8`, env-var dictionary, concurrent stdout+stderr+wait via `Task.WhenAll` (avoids pipe-buffer deadlock on large payloads), cancellation that calls `proc.Kill(entireProcessTree: true)` then rethrows. Closes stdin immediately so any unexpected interactive prompt EOFs.
- [ ] Single-instance guard: `using var mutex = new Mutex(true, @"Global\AiDaemon", out var owned); if (!owned) { logger.LogCritical("Another instance running"); return; }`. Held for process lifetime. SQLite file lock is not enough.
- [ ] Empty `Worker` that ticks every `PollIntervalSeconds` and logs. Loop body checks for `%ProgramData%\AiDaemon\PAUSED` first and skips the tick if present.
- [ ] Create `appsettings.Local.json` (next to `appsettings.json`, gitignored) with `{ "Daemon": { "Ntfy": { "Topic": "<uuid>" } } }`. Wire it in `Program.cs` via `builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)`.
- [ ] Confirm `gh auth status` reports the account `AiUserLogin` references (run `gh auth login` if not).

**Acceptance:** `dotnet run` prints "tick" every minute. `Ctrl+C` shuts down cleanly.

### Phase 1 — Polling + state (~1 evening)

**Goal:** daemon polls `/notifications` via `gh api`, persists what it has seen, logs each new notification, doesn't re-process across restarts.

- [ ] `IGhClient` / `GhClient` — the only thing in the daemon that talks to GitHub. Depends on `IProcessRunner`:
  - `Task<T> ApiAsync<T>(string path, CancellationToken)`: invokes `IProcessRunner.RunAsync` with `gh`, args `["api", path]`. Deserializes stdout to `T`. Throws on non-zero exit; on auth failure (401/403 in stderr, or `gh auth login` hint, or `GH_TOKEN` hint), pushes a high-priority ntfy alert so Jon knows to refresh `gh auth login`.
  - `Task<List<GhNotification>> ListNotificationsAsync(CancellationToken)`: calls `/notifications?participating=true&all=false`.
  - `Task MarkThreadReadAsync(string threadId, CancellationToken)`: `gh api -X PATCH /notifications/threads/<id>`.
  - `Task<CommentInfo> GetCommentAsync(string url, CancellationToken)`: dereferences `subject.latest_comment_url`.
  - `Task<PrInfo> GetPullRequestAsync(string repo, int prNumber, CancellationToken)`.
  - ~50 lines total once `IProcessRunner` is doing the heavy lifting.
- [ ] `SqliteStateStore` implementing `IStateStore` (Dapper). Migrations on startup. Open every connection with `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;` (default rollback journal will fight the worker + sweep timer). Connection-per-call is fine with WAL. Methods: `IsProcessed(threadId, commentId)`, `MarkProcessed(...)`, `GetBranchState`, `UpsertBranchState`, `ListActiveBranches()`, `IncrementRateLimit`, `GetRateLimitToday`. (No poll cursor needed — `gh` returns only unread when we use `all=false`, and we mark threads read after dispatch, so the inbox is the cursor.)
- [ ] `NotificationPoller.PollAsync(ct)`: yields notifications from `GhClient.ListNotificationsAsync` that aren't in the `processed` table.
- [ ] `Worker.ExecuteAsync`: `await foreach (var n in poller.PollAsync(ct))` → log → `MarkProcessed("seen")` for now.
- [ ] Unit tests: mock `IGhClient`, verify idempotency.

**Acceptance:** start daemon, comment on a test issue from another account, see daemon log the notification within one cycle. Restart daemon, confirm it doesn't re-process.

**Why no `If-Modified-Since`?** GitHub's authenticated rate limit is 5000/hour; one poll/minute = 60/hour. Bandwidth is ~50KB per full poll, fine at this rate. Conditional GET via `gh api -i` is doable later if it ever matters; not worth the header-parsing complexity in v1.

### Phase 2 — Triage pipeline (~1 evening)

**Goal:** every notification produces a `TriageVerdict { Action, Confidence, Summary }` where `Action ∈ { Drop, Actionable }`. L1+L2 are deterministic; L3 (optional) calls Claude Haiku.

- [ ] `GhClient.GetCommentBody(notification)`: dereferences `subject.latest_comment_url` → comment body + author login.
- [ ] `TriagePipeline.TriageAsync(notification, ct)`:
  - **L1 — author/type filter:**
    - Drop if `subject.type` ∉ {`Issue`, `PullRequest`}.
    - Drop if author == `AiUserLogin`.
    - Drop if author ∈ `BotAuthorBlocklist`.
    - Drop if `reason` ∉ `ActionableReasons`.
    - Drop if `IncrementRateLimit(thread, today)` would exceed `MaxActionsPerThreadPerDay`.
    - **Bypass L2/L3 for `review_requested`**: a review request is inherently actionable and has no comment body to feed L3. Mark `Action=Actionable, Why="review_requested"` and return.
  - **L2 — content filter:** drop if body matches any `L2DropPatterns` regex (case-insensitive, after stripping GitHub quoted-reply lines — lines starting with `>` and the blank line that follows them).
  - **L3 — LLM triage:** invoke `IProcessRunner` with `claude -p --bare --model haiku --output-format json --json-schema <schema> --system-prompt <prompt> <input>`. Parse `result.structured_output`. **Before coding this**, run the canary tests in the verification checklist to confirm `--bare` and `--json-schema` exist in the installed Claude Code version and that `result.structured_output` is the actual response field. Fallback if any of those fail: drop `--bare` (slower startup, otherwise identical), drop `--json-schema` (parse and validate the JSON shape ourselves with `JsonSchema.Net` against the schema embedded resource), or read whichever wrapper field actually carries the structured output. Returns a candidate verdict that gets filtered through the bias rule below.
- [ ] **L3 asymmetric bias.** False-drop is strictly worse than false-actionable for this user (silent loss of a real ask vs a dismissable phone buzz). After L3 returns:
  - Honor `action: "drop"` only when `confidence ≥ 0.8` AND comment body has no `?` AND no @-mention of `jon-or-ai`. Otherwise, override to `Actionable`.
  - Log every L3 outcome at Information level with `thread_id`, `verdict.action`, `verdict.confidence`, `verdict.why`, AND the full comment body. **The full body on Drop is the audit trail** — without it there's no way to spot-check L3's drops a week later.
- [ ] `ClaudeRunner.RunHeadlessAsync(args, stdin, cwd, ct)` thin wrapper around `IProcessRunner` for `claude -p` invocations. Used by L3 only; the dispatcher never calls it. Verify `--bare` exists on the installed Claude Code version before relying on it (not in recipe.md's flag table; falls back to plain `-p` if missing).
- [ ] Triage system prompt + JSON schema as embedded resources (`<EmbeddedResource Include="Triage\*.*" />` in csproj). Schema:
  ```json
  {
    "type":"object",
    "required":["action","confidence","summary"],
    "properties":{
      "action":{"enum":["actionable","drop"]},
      "confidence":{"type":"number","minimum":0,"maximum":1},
      "summary":{"type":"string","maxLength":200},
      "why":{"type":"string"}
    }
  }
  ```
- [ ] Tests: L1+L2 with table-driven cases (including a `review_requested` shortcut case and a quoted-reply stripping case); mock `IProcessRunner` for L3 to return canned JSON; verify the asymmetric bias correctly upgrades low-confidence drops.

**Acceptance:** post a `lgtm` comment → dropped at L2. Post a "fix the typo on line 42" comment → `actionable`. Post "what should we do about the auth flow?" → `actionable`. A "just FYI, no action needed" comment → if L3 returns `drop` with confidence ≥ 0.8 and no `?`, dropped; otherwise upgraded to actionable. Request a review on a PR → `actionable` without an L3 call. The log shows full comment body on every Drop verdict.

### Phase 3 — Branch resolution (~half evening)

**Goal:** `BranchResolver.Resolve(notification)` returns `BranchInfo { Repo, Branch, Worktree, Pr?, Issue? }` or `null` if no worktree exists.

- [ ] For `PullRequest`: fetch PR via `IGhClient.GetPullRequestAsync` (which shells `gh api /repos/<repo>/pulls/<n>`), read `head.ref`.
- [ ] For `Issue`: parse issue number from `subject.url`, glob `<WorktreeRoot>\<issue>-*`, read `git -C <worktree> rev-parse --abbrev-ref HEAD` to confirm.
- [ ] Validate worktree path exists and is on the right branch.
- [ ] Return `null` for "no worktree" (signal: never started, skip silently and mark read).
- [ ] Validate `repo` against `RepoAllowlist`. Out-of-scope → log, do **not** mark read (leave for human).

**Acceptance:** notification on issue 16119 with worktree `~/worktrees/16119-isdpvirtualproperty` → resolves correctly. Notification on issue 99999 with no worktree → returns null. Notification on a non-allowlisted repo → returns null and logs the skip.

### Phase 4 — Dispatch + RC primitives (~weekend)

**Goal:** `Dispatcher.DispatchAsync(branch, notification, verdict)` either spawns an RC session and pushes the URL, or pushes a heads-up if RC is already alive. RC spawn / capture / push works end-to-end per recipe.md.

- [ ] `RcLauncher` translating recipe.md to C#:
  - `Task<string> SeedSessionAsync(BranchInfo, string initialPrompt, CancellationToken)`:
    - Generates `Guid.NewGuid()` for sessionId.
    - Runs `claude -p --session-id <uuid> --output-format json --model haiku --permission-mode bypassPermissions <prompt>` in `branch.Worktree` via `IProcessRunner`.
    - Returns the UUID. The seed prompt is brief context-setting only ("Branch <branch>; you'll be invoked when GitHub events arrive — wait for the user").
  - `Task<RcAttachment> SpawnRcAsync(BranchInfo, string sessionId, CancellationToken)`:
    - Spawn PowerShell with **`UseShellExecute=false, CreateNoWindow=false, WindowStyle=Normal`** (NOT `UseShellExecute=true` — the env-var dictionary is ignored under shell execute, and `Process.Id` may return a launcher PID instead of the real `powershell.exe` PID). Args: `-NoExit -Command "claude --resume <sid> --remote-control <name>"`. `WorkingDirectory = branch.Worktree`. This both yields a visible terminal window AND a reliable `proc.Id`.
    - Find child `claude.exe`: WMI query `Win32_Process WHERE ParentProcessId=<ps_pid> AND Name='claude.exe'`. Retry every 500ms for up to 15s.
    - Read `%USERPROFILE%\.claude\sessions\<claude_pid>.json` with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` (the writer holds it briefly during updates). Wrap reads in try/catch for `IOException`, `JsonException`, and `FileNotFoundException` and retry. Poll for up to 15s waiting for `bridgeSessionId` to populate AND be non-null.
    - Capture `claudeProc.StartTime.Ticks` at this point and persist it alongside the PID — Windows recycles PIDs and a long-running daemon can be fooled by a recycled one without the start-time check.
    - Returns `RcAttachment { PsPid, ClaudePid, ClaudeStartTicks, BridgeSessionId, Url }`.
    - Writes `<branch.Worktree>\.daemon-active` containing `{ sessionId, bridgeSessionId, ts }`. **Purpose:** signals to Jon (and to any future `claude --resume` he might run by hand from the worktree) that a daemon-spawned RC session owns this conversation. README must warn: don't run `claude --resume <sid>` against a worktree containing `.daemon-active` — that violates the single-writer invariant.
  - `Task CleanupAsync(BranchState rec, CancellationToken)`: kills the PowerShell process tree (which takes the child `claude.exe` with it), removes `<rec.Worktree>\.daemon-active`, leaves the JSONL intact. Called from sweep (case 1 and case 2), the dispatcher's stale-row branch, and graceful shutdown.
  - `bool IsAlive(int pid, long startTicks)`:
    1. `Process.GetProcessById(pid)` succeeds AND `p.StartTime.Ticks == startTicks`. PID-recycle defense.
    2. AND the registry file `%USERPROFILE%\.claude\sessions\<pid>.json` still contains a non-null `bridgeSessionId`. Detects bridge-dead-but-process-alive (the 10-min outage scenario from recipe.md line 187 — the relay tears down but the local process can stay up).
    Anything short of both → treat as not-alive.
- [ ] `Dispatcher.DispatchAsync`:
  ```
  if rec.Mode == RcActive AND IsAlive(rec.RcClaudePid, rec.RcClaudeStart):
      // session already open AND bridge healthy — push heads-up
      pusher.PushHeadsUp(rec.RcUrl, branch, notification, verdict)
      return "heads_up"

  // either Idle, or RcActive but dead/stale — fall through to (re-)spawn
  if rec.Mode == RcActive:
      await rcLauncher.CleanupAsync(rec, ct)
      rec.Mode = Idle; rec.RcPid = null; rec.RcClaudePid = null; rec.RcClaudeStart = null;
      rec.RcBridgeId = null; rec.RcUrl = null;

  if rec.SessionId is null:
      rec.SessionId = await rcLauncher.SeedSessionAsync(branch, initialPrompt(branch))

  rec.RcAttachment = await rcLauncher.SpawnRcAsync(branch, rec.SessionId)
  rec.Mode = RcActive
  state.UpsertBranchState(rec)
  pusher.PushSessionLink(rec.RcUrl, branch, notification, verdict)
  return "escalated"
  ```
- [ ] **Background sweep (every 60s)** walks `RcActive` rows and reaps in two cases:
  1. `!IsAlive(rc_claude_pid, rc_claude_start)` → process or bridge died externally. `await rcLauncher.CleanupAsync(rec, ct)`, reset to `Idle`, clear rc_* fields. `session_id` is preserved so the next event resumes the same conversation.
  2. **Idle timeout (`RcIdleTimeoutHours`, default 2):** if the session's JSONL at `~/.claude/projects/<encoded-cwd>/<sid>.jsonl` hasn't been written to in `RcIdleTimeoutHours` hours, `await rcLauncher.CleanupAsync(rec, ct)` and reset state. JSONL `last write time` is the source of truth for "is anyone driving this?" — it ticks on every assistant turn and tool call. Bounds resource usage and prevents stale-URL traps (a respawn produces a fresh `bridgeSessionId`).
- [ ] **Daemon startup**: on boot, walk every `RcActive` row, call `IsAlive`, reap any that fail. The PIDs from before the reboot are dead by definition; this is just bookkeeping.
- [ ] Tests: mock `IProcessRunner` and an `IFileSystem` abstraction (so registry-file polling is testable). Cover: first event on branch (seed + spawn + push URL), second event with RC alive (heads-up only), event after RC process died (cleanup + respawn + push new URL), event after bridge-dead-but-process-alive (cleanup + respawn), idle-timeout sweep (RC reaped after 2h of no JSONL writes), startup reconciliation.

**Acceptance:** with a real worktree, post a substantive comment → daemon seeds + spawns RC → ntfy push fires with the URL → opening URL on phone shows the live session in the right worktree. Post a follow-up comment while the session is open → second push fires as a heads-up, no new session spawned. Close the session, post another comment → daemon respawns RC with `--resume <sid>` and conversation history is intact.

### Phase 5 — Ntfy push (~half evening)

**Goal:** `NtfyPusher` posts session links and heads-ups with action buttons.

- [ ] `NtfyPusher.PushSessionLinkAsync(url, branch, verdict)`:
  ```
  POST https://ntfy.sh/<topic>
  Title: "[<repo>:<branch>] <verdict.summary>"
  Priority: 4
  Tags: "robot"
  Click: <rc-url>
  Actions: "view, Open session, <rc-url>"
  Body: <verdict.why or comment excerpt>
  ```
- [ ] `NtfyPusher.PushHeadsUpAsync(url, branch, verdict)`: same shape, lower priority (3), title prefixed `[update]`.
- [ ] Use `IHttpClientFactory`; wire a typed client with the ntfy base URL.
- [ ] Test with `ntfy subscribe` on phone before integrating into Dispatcher.

**Acceptance:** phone receives a notification with the RC URL. Tapping it opens the live session.

### Phase 6 — Polish, service install, runbook (~half evening)

**Goal:** safe to leave running unattended.

- [ ] Periodic prune of `processed` rows older than 30d.
- [ ] Auth health check on startup: `gh auth status` (parses output) and `gh api /user` for live verification. Surface a high-priority ntfy push if it fails.
- [ ] Configure Serilog: console sink + rolling-daily file sink with `rollOnFileSizeLimit: true, fileSizeLimitBytes: 50_000_000, retainedFileCountLimit: 14`. Log path uses `Environment.GetFolderPath(SpecialFolder.CommonApplicationData)` (`C:\ProgramData\AiDaemon\logs`) — service-identity-safe, doesn't depend on `%LOCALAPPDATA%`.
- [ ] **Publish + install**:
  - `dotnet publish -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishReadyToRun=true -c Release -o publish/`
  - `sc.exe create AiDaemon binPath= "C:\Tools\AiDaemon\AiDaemon.exe" start= auto obj= ".\Jon" password= "<pwd>"` — note the **mandatory space after each `=`** in `sc.exe`. The `obj=".\Jon"` is critical: under default `LocalSystem`, `%USERPROFILE%` resolves to `C:\Windows\System32\config\systemprofile`, breaking `~/.claude/sessions/<PID>.json` reads, WMI cross-session lookups, and Claude's auth/trust state.
  - `sc.exe failure AiDaemon reset= 86400 actions= restart/5000/restart/5000/restart/30000` for crash auto-restart.
- [ ] **PATH for service identity**: `claude` and `gh` must be reachable from Jon's user PATH (typical when installed via npm/winget). Either set absolute paths in `appsettings.json` (`ClaudePath: "C:\\Users\\Jon\\AppData\\Roaming\\npm\\claude.cmd"`) or augment the service environment via the registry under `HKLM\SYSTEM\CurrentControlSet\Services\AiDaemon\Environment`. **Absolute paths are the more reliable path** — set them once, never depend on PATH inheritance.
- [ ] **Runbook section in README** for "I'm not getting pings":
  - Where logs live (`C:\ProgramData\AiDaemon\logs\`).
  - Common greps: `"polled.*count="`, `"NtfyPusher"`, `"L3 verdict"`, `"escalated"`.
  - How to inspect state DB: `sqlite3 C:\ProgramData\AiDaemon\state.db "select * from branches"`.
  - How to fire a test ntfy: `Invoke-RestMethod -Uri "https://ntfy.sh/<topic>" -Method Post -Body "test"`.
  - How to verify gh auth: `gh auth status` and `gh api /user`.
  - How to pause / unpause: drop / remove `C:\ProgramData\AiDaemon\PAUSED`.
  - How to rotate ntfy topic: edit `appsettings.Local.json`, restart service, resubscribe in app.
  - **Warning: don't `claude --resume <sid>`** against a worktree containing `.daemon-active` — violates single-writer invariant.

**Acceptance:** install as a Windows Service running as `.\Jon`, reboot, verify it's polling within one minute of login. Drop a `PAUSED` file, verify the next tick logs "paused" and skips polling. Trigger an auth failure (e.g., revoke the gh token momentarily) and confirm a high-priority ntfy push fires.

## Cross-cutting decisions

### Single-writer invariant (recipe.md gotcha)

**Within the daemon:** never runs `claude -p --resume <sid>` against a branch's session. The only `-p` calls are L3 triage (different short-lived sessions, no `--resume`) and the one-shot session seed (before RC ever attaches). RC is the only long-lived writer per branch.

**At the human boundary:** Jon could violate the invariant from his own terminal by running `claude --resume <sid>` against an actively-daemoned worktree. Defense-in-depth: `RcLauncher.SpawnRcAsync` writes a `.daemon-active` JSON marker into the worktree containing the active sessionId. README warns against `--resume`-ing it. The marker is removed on every cleanup (sweep, idle timeout, dispatcher reaping a stale row).

This sidesteps recipe.md's line 155 footgun for the daemon's own paths "by design"; for human-initiated coexistence, the marker file + README are the safety net.

### DI lifetimes

Every service in `Services/`, `Storage/`, and `Process/` is a singleton — no per-request lifetimes in a worker host:

- Singleton: `IGhClient`, `IRcLauncher`, `IClaudeRunner`, `INotificationPusher`, `IStateStore`, `IBranchResolver`, `ITriagePipeline`, `INotificationPoller`, `IDispatcher`, `IProcessRunner`, `IFileSystem`.
- `HttpClient` for `NtfyPusher` via `services.AddHttpClient<NtfyPusher>()` — DI manages its lifetime; do not dispose manually.
- `IOptions<DaemonOptions>` is fine. Don't reach for `IOptionsMonitor` — config doesn't reload at runtime in v1.

### Cancellation propagation

`stoppingToken` from `BackgroundService.ExecuteAsync` flows through every async method. Rules:

- All async methods take a `CancellationToken` parameter (no defaulting to `CancellationToken.None`).
- No `Task.Run` anywhere in the codebase — it swallows tokens and adds thread-pool overhead with no benefit.
- `IProcessRunner.RunAsync` is the only place that wraps `WaitForExitAsync(ct)`; on cancel it calls `proc.Kill(entireProcessTree: true)` and rethrows. Long-running shell-outs are otherwise zombies on shutdown.
- Per-operation timeouts compose with the global token via `CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, perOpCts.Token)`.

### Process supervision

Each RC spawn writes `rc_pid` and `rc_claude_pid` immediately after `SpawnRcAsync` returns. The 60s sweep reaps dead RC processes by checking `IsAlive` and resetting state. Daemon restarts also re-validate every `RcActive` row on startup before resuming.

### Worktree mismatch handling

If `BranchResolver` finds a worktree but its current branch ref doesn't match the expected name (someone force-pushed, renamed, or `git worktree remove`d it), log a warning and skip. Don't try to repair filesystem state from the daemon.

### What the daemon will not do

- Edit files
- Run shell commands beyond what L3 triage needs (and triage has no shell tools)
- Post GitHub comments
- `git commit` / `git push`
- Auto-merge, auto-close, auto-label

Every action against the world is taken by the user inside the RC session. The daemon's authority is bounded to: read GitHub notifications, read comment bodies, classify them, mark them read, spawn an RC process in an existing worktree, push a URL to the user's phone.

### Logging

Every loop iteration, every notification verdict, every dispatch outcome, every spawn/teardown: structured Serilog event with `branch`, `thread_id`, `outcome`. The log is the audit trail — no separate event log needed for v1.

## What's explicitly out of scope for v1

- **Autonomous handling.** The daemon never edits, comments, or pushes on its own. Every action goes through an RC session the user drives.
- **Auto-cleanup of merged branches.** SQLite rows + `~/.claude/projects/<encoded-cwd>/<sid>.jsonl` files persist forever. Cleanup is manual: a `daemon prune` subcommand or hand-deletion. Decision: simpler is better at v1; revisit if accumulation becomes a problem.
- **Auto-creating worktrees.** The user creates worktrees the way they always have. Notifications for issues without an existing worktree are silently dropped (and marked read).
- Multi-machine / shared state. State is local to one Windows box.
- Webhook receiver. Polling is the only event source.
- RC server mode. Broken on 2.1.138 per recipe.md.
- Stream-json broker. Not needed when the daemon never drives Claude in a loop.
- Multiple AI identities or repos beyond the allowlist.
- A web UI. The `PAUSED` file + log file + sqlite browser is enough management surface.
- Heads-up coalescing (5 simultaneous comments on one PR = 5 ntfy buzzes). Acceptable trade for v1; rate-limit catches runaway cases. Revisit if push fatigue is real.
- Per-repo / partial pause (only `PAUSED` global). Defer.

## First-run sequence

```
1. Create `appsettings.Local.json` with the ntfy topic
2. Run `gh auth login` (one-time) as the account `AiUserLogin` references
3. dotnet run --project src/AiDaemon
4. Subscribe to <topic> in the ntfy app on your phone
5. From your *human* GitHub account (not jon-or-ai), comment "@jon-or-ai please fix the typo in line 42 of foo.cs" on a PR in an allowlisted repo where a worktree exists.
6. Watch the log: notification → L1 pass → L2 pass → L3 actionable → seed session → spawn RC → ntfy push fires → tap it on phone → drive the session.
7. Post a follow-up comment while the session is open → second push as a heads-up; same session, no respawn.
8. Leave the session idle for 2 hours → the sweep kills the RC process; state goes back to Idle. A new comment respawns RC with `--resume` and conversation history is intact.
```

If any of those six steps fails, the log line for that step tells you which service threw, what the input was, and what the verdict (or error) was.

## Verification checklist before writing any code

- [ ] Confirm the recipe-validated Claude Code version (2.1.138) is still current; flag if a newer release changed `~/.claude/sessions/<PID>.json` shape.
- [ ] Confirm global `gh auth status` reports the account `AiUserLogin` is set to. Verify with `gh api notifications` returning that inbox.
- [ ] Confirm `claude -p --bare --model haiku` works on the daemon's user account. **`--bare` is not in recipe.md's flag table** — verify it actually exists in the installed version, otherwise fall back to plain `-p` and accept the slower startup.
- [ ] **Verify `--json-schema` flag exists** in the installed version (not in recipe.md's flag table). If missing, the L3 fallback is to validate the JSON shape against the embedded schema with `JsonSchema.Net` after the fact.
- [ ] **Verify `structured_output` JSON path:** run a one-off `claude -p --output-format json --json-schema '<schema>' "test"` and inspect where the schema-validated payload lands in the response wrapper. Could be `result`, `result.structured_output`, top-level `structured_output`, or a string of JSON to re-parse. Get this wrong and L3 silently fails.
- [ ] **Verify PowerShell PID source:** spawn a test PowerShell with `Process.Start` (`UseShellExecute=false, CreateNoWindow=false`) and confirm `proc.Id` matches the visible window's `powershell.exe` PID under `Get-CimInstance Win32_Process` lookup, not a launcher PID.
- [ ] **Check claude.ai concurrent-session limits.** If Max-tier caps concurrent RC sessions at, say, 3, the design needs to know — five idle branches on a flight day would hit the wall. Open a couple of `claude --remote-control` sessions simultaneously as a smoke test.
- [ ] Pick the ntfy topic UUID and confirm push delivery to phone.
- [ ] Decide install path. `C:\Tools\AiDaemon` (or `C:\Program Files\AiDaemon`) for the binary; state under `C:\ProgramData\AiDaemon` for service-identity-safe paths. Avoid `%LOCALAPPDATA%`.

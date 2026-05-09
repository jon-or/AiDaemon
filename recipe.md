s# Drive a local Claude Code session that the user can also reach via Remote Control

A repeatable recipe for spawning a `claude` session that:

- has a **session UUID you control** (so you can resume it from `-p` calls later),
- has **Remote Control active** (so a human gets a `claude.ai/code/session_…` URL to drive it from a phone or browser),
- and lets you **read the relay URL programmatically** (no TTY scraping, no UI automation).

This is the "I drive headlessly + the user has a clickable link" pattern. Verified end-to-end on Claude Code v2.1.138 on Windows 11 (Git Bash + PowerShell), but the underlying mechanics are platform-portable.

## When to use this

Use this when you want both:

1. A Claude Code session a parent process can drive non-interactively (`claude -p --resume <uuid>`), and
2. A live bridge so a person can connect from `claude.ai/code` or the Claude mobile app and watch / steer the same session.

If you only need (1), skip Remote Control entirely and just use `claude -p --session-id <uuid> "..."` followed by `claude -p --resume <uuid> "..."`. The broker pattern below is for keeping a single subprocess hot across many turns.

## Concepts

Two distinct IDs travel together. Confusing them costs hours.

| ID | Where it lives | What it is | Stable across? |
|---|---|---|---|
| `sessionId` (a UUID) | `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl` filename, also the `sessionId` field in `~/.claude/sessions/<PID>.json` | The local conversation. Pass to `--session-id` (new) or `--resume` (existing). | The whole life of the conversation — across closes, reopens, machines, etc. |
| `bridgeSessionId` (e.g. `session_01CB6duHUCCEQzuT336LxqJ8`) | `bridgeSessionId` field in `~/.claude/sessions/<PID>.json` | The Anthropic relay's handle for one Remote Control attachment. Embedded in the `claude.ai/code/session_…` URL. | One RC attachment only — close the local process, the next attachment mints a brand new `bridgeSessionId`. The conversation underneath is unaffected. |

The local conversation is persisted as one JSONL file under `~/.claude/projects/<encoded-cwd>/`, where `<encoded-cwd>` is the cwd with each path separator (and the drive colon on Windows) replaced by `-`. For `C:\Users\Jon\AppData\Local\Temp\rc-resume-test`, the directory is `C--Users-Jon-AppData-Local-Temp-rc-resume-test`. **Resume is scoped to the current cwd's project directory**, so a resume launched from a different cwd will report `No conversation found with session ID: …` even though the JSONL is sitting on disk.

## Recipe

Three pieces: seed the session, spawn it with RC, capture the URL.

### 1. Seed the session with a UUID you control

```bash
# Pick a working directory. Stick to it for every later resume.
WD="C:/Users/Jon/AppData/Local/Temp/my-driveable-session"
mkdir -p "$WD"

# Generate a UUID. PowerShell: [guid]::NewGuid().ToString()
SID="dbf3632b-56fb-4ff3-b48a-4d0112a45f63"

cd "$WD"
claude -p \
  --session-id "$SID" \
  --output-format json \
  --model haiku \
  --permission-mode bypassPermissions \
  "<your initial prompt>"
```

The JSONL appears at:

```
~/.claude/projects/<encoded-WD>/<SID>.jsonl
```

You can `tail -f` that file to observe further turns later.

### 2. Spawn with `--resume <uuid> --remote-control <name>` in a real terminal

Remote Control needs an interactive TTY at startup — under `--print` it is silently ignored, and `/remote-control` typed as a stream-json user message is treated as plain text (slash commands are dispatched by the CLI's TTY input layer, not by the conversation pipeline). On Windows, the most reliable spawn is **`Start-Process -WorkingDirectory`** (more reliable than `wt.exe -d`, which can drop the cwd):

```powershell
$wd     = 'C:\Users\Jon\AppData\Local\Temp\my-driveable-session'
$sid    = 'dbf3632b-56fb-4ff3-b48a-4d0112a45f63'
$rcName = 'my-driveable-session'

$cmd = "claude --resume $sid --remote-control $rcName"

$proc = Start-Process `
    -FilePath powershell.exe `
    -PassThru `
    -WorkingDirectory $wd `
    -ArgumentList @('-NoExit','-Command',$cmd)
```

The new window prints a status line like:

```
/remote-control is active · Code in CLI or at https://claude.ai/code/session_01CB6duHUCCEQzuT336LxqJ8
```

### 3. Read the `bridgeSessionId` from the per-PID registry

Within roughly half a second of startup, claude writes `~/.claude/sessions/<PID>.json` containing both IDs:

```json
{
  "pid": 41848,
  "sessionId":  "dbf3632b-56fb-4ff3-b48a-4d0112a45f63",
  "bridgeSessionId":  "session_01CB6duHUCCEQzuT336LxqJ8",
  "cwd":  "C:\\Users\\Jon\\AppData\\Local\\Temp\\my-driveable-session",
  "kind":  "interactive",
  "status":  "idle",
  "version":  "2.1.138"
}
```

Find the inner `claude.exe` (it's a child of the powershell you spawned) and poll the registry until `bridgeSessionId` shows up:

```powershell
# Find the claude.exe child of the powershell we just spawned.
$claudePid = $null
for ($i = 0; $i -lt 30; $i++) {
    $c = Get-CimInstance Win32_Process `
            -Filter "ParentProcessId=$($proc.Id) AND Name='claude.exe'" `
            -EA SilentlyContinue
    if ($c) { $claudePid = $c.ProcessId; break }
    Start-Sleep -Milliseconds 500
}

# Wait for bridgeSessionId to populate (RC registers with the relay async).
$reg = "$env:USERPROFILE\.claude\sessions\$claudePid.json"
$info = $null
for ($i = 0; $i -lt 30; $i++) {
    if (Test-Path $reg) {
        $j = Get-Content $reg -Raw | ConvertFrom-Json
        if ($j.bridgeSessionId) { $info = $j; break }
    }
    Start-Sleep -Milliseconds 500
}

"https://claude.ai/code/$($info.bridgeSessionId)"
```

That URL is what you hand to the user. They open it in any browser or the Claude mobile app and they're driving the live local session — all tools, MCP servers, the seeded conversation.

## Re-attaching after a close

When the user closes the window, the local `claude` process exits and the relay tears the bridge down. The JSONL is intact. To reattach:

1. Run step 2 again with the **same** `$sid` (the local UUID is stable).
2. Run step 3 again with the **new** `$claudePid` to read a **new** `bridgeSessionId`.

The new URL points at the same conversation underneath. The old URL is dead — the relay invalidates `bridgeSessionId` per attachment.

## Driving the session from a parent agent (headless turns)

The same `sessionId` is drivable from outside the live RC window via `claude -p --resume`:

```bash
cd "$WD"   # cwd-scoped lookup, must match seed's project dir
claude -p \
  --resume "$SID" \
  --output-format json \
  --model haiku \
  --permission-mode bypassPermissions \
  "<follow-up prompt>" \
  | jq -r .result
```

Caveat: **single-writer lock**. While a `--remote-control` window holds the session active and a human is mid-turn, an external `-p --resume` may either queue, fail, or mutate state under the user's hands depending on timing. In practice: drive headlessly when no human is connected, then hand back via the RC URL, and vice versa. Coordinated rather than concurrent.

## Long-lived stream-json broker (drive without re-spawning)

The `-p --resume` pattern reboots the claude subprocess for every turn (warm prompt cache mitigates cost, but you pay startup latency every time). For a long sequence of programmatic turns without RC, use the documented stream-json duplex mode — same protocol the VS Code extension uses internally:

```bash
claude \
  --print \
  --input-format stream-json \
  --output-format stream-json \
  --include-partial-messages \
  --verbose \
  --permission-mode bypassPermissions \
  --model haiku
```

Each line on stdin is one user message wrapped as `{"type":"user","message":{"role":"user","content":[{"type":"text","text":"…"}]}}`. Each line on stdout is one event: `system`, `assistant`, `user` (tool result), `result`, `stream_event`, `rate_limit_event`, …

Wrap that in a small TCP broker (~150 lines of Node) so a parent process can `connect → send → read until result → disconnect` per turn. Reference implementation pattern: a TCP server on `127.0.0.1:<port>` that pipes stream-json between connected clients and one long-lived claude subprocess.

For tool approvals, swap `--permission-mode bypassPermissions` for `--permission-prompt-tool stdio` (what the VS Code extension uses) and the broker will receive `control_request` events the parent can answer over the same TCP channel.

**`--remote-control` does not compose with stream-json mode.** `--print` reserves stdout for events, leaving no channel for the RC banner, and the relay registration is silently skipped. If you want both the broker pattern and an RC URL, run two processes: one stream-json-driven for headless turns, one TTY-launched for RC. They can share neither the live process nor the lock — only the JSONL persists.

## Gotchas

- **`--resume` lookup is cwd-scoped.** The window has to launch from the same cwd the seed was created in. `Start-Process -WorkingDirectory` is reliable; `wt.exe -d <path>` proved unreliable (cwd silently dropped). If you see `No conversation found with session ID: <uuid>` even though the JSONL is on disk, this is the cause 9 times out of 10.
- **`--remote-control` flag at startup is silently ignored under `--print` and stream-json.** Verified by inspecting `~/.claude/sessions/<PID>.json`: the `bridgeSessionId` field is never written. RC requires the CLI's interactive REPL.
- **`/remote-control` slash command does NOT work via stream-json input.** Slash commands are intercepted by the CLI's TTY input layer; messages injected on stdin go straight into the conversation. The model sees `/remote-control` as plain text.
- **`claude remote-control` server mode** (the long-lived multi-session daemon) is currently broken on v2.1.138: pre-create-session POSTs include a deprecated `source` field that the relay rejects with `400 source: Extra inputs are not permitted`. The environment URL prints, but with no sessions registered it just bounces the user to `/code`.
- **Git Bash on Windows mangles `/foo` arguments to `C:/Program Files/Git/foo`.** When passing slash-leading strings (slash commands as prompts, paths) to `claude` via the `Bash` tool, prefix the command with `MSYS_NO_PATHCONV=1`.
- **`bridgeSessionId` populates asynchronously.** The relay registration takes ~200–800 ms after process start. Poll the registry; don't read once and assume.
- **Each RC attachment mints a new `bridgeSessionId`.** Caching the URL across closes does not work — you must re-read after each respawn.
- **Single-writer lock.** Two concurrent writers (e.g. an `-p --resume` while a `--rc` window has a human typing) will fight. Coordinate explicitly.

## Cleanup

```bash
# Close the RC window manually, then:

# Remove the temp working directory and its conversation history.
rm -rf "C:/Users/Jon/AppData/Local/Temp/my-driveable-session"
rm -rf "$HOME/.claude/projects/C--Users-Jon-AppData-Local-Temp-my-driveable-session"

# If you spawned worktrees:
git worktree remove --force <path>
git branch -D <branch>
```

The relay-side `bridgeSessionId`s expire on their own once the local process is gone — no manual cleanup needed.

## Reference: useful CLI flags

| Flag | Effect |
|---|---|
| `--session-id <uuid>` | Pin the conversation's local UUID at creation. Must be a fresh UUID. |
| `--resume <uuid>` | Reload an existing JSONL. Cwd-scoped lookup. |
| `--remote-control [name]` | Activate the RC bridge. Interactive only — needs a TTY. The optional `name` becomes the session title in claude.ai/code. |
| `--add-dir <dir>` | Grant tool access to a directory outside the cwd. |
| `--permission-mode <mode>` | `default`, `acceptEdits`, `auto`, `bypassPermissions`, `plan`, `dontAsk`. For non-interactive `-p`, only `bypassPermissions` and `dontAsk` (with an allowlist) avoid silent stalls. |
| `--allowed-tools "Read Edit Bash(git diff:*)"` | Allowlist for `dontAsk` mode. |
| `--permission-prompt-tool stdio` | Route per-tool approvals through stream-json `control_request` events instead of using a built-in mode. The supported way to gate tool calls from a parent. |
| `--input-format stream-json` / `--output-format stream-json` | Long-lived NDJSON duplex. Documented on `--print` only, but works without `--print` too — that's what the VS Code extension uses. |
| `--include-partial-messages` | Stream `stream_event` deltas so you see assistant text as it arrives. |
| `--debug-file <path>` | Write the bridge's debug log to a file. Useful when the URL doesn't print and you need to see why. |

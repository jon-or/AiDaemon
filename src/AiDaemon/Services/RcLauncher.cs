using System.Diagnostics;
using System.Management;
using System.Text.Json;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SysProcess = System.Diagnostics.Process;

namespace AiDaemon.Services;

public class RcLauncher : IRcLauncher
{
    /// <summary>How long to poll WMI looking for the <c>claude.exe</c> child of the spawned cmd.exe.</summary>
    static readonly TimeSpan ChildLookupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to poll the per-PID registry for <c>bridgeSessionId</c>. The relay usually
    /// registers in 1-2s when healthy, but we've seen it take longer under load — at 5s the
    /// dispatcher was firing spurious "Not Available" pushes for sessions that ended up
    /// registering a few seconds later. 10s is enough headroom to ride those out without
    /// blocking the tick for half a minute when the relay is genuinely down.
    /// </summary>
    static readonly TimeSpan RegistryPollTimeout = TimeSpan.FromSeconds(10);

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly IFileSystem _fs;
    readonly DaemonOptions _options;
    readonly ILogger<RcLauncher> _logger;

    public RcLauncher(
        IFileSystem fs,
        IOptions<DaemonOptions> options,
        ILogger<RcLauncher> logger)
    {
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RcAttachment> SpawnRcAsync(BranchInfo branch, string? sessionId, CancellationToken cancellationToken)
    {
        var rcName = SafeRcName(branch.Branch);

        // The command line cmd.exe runs once the new console window comes up. Fresh spawn
        // (no sessionId): just --remote-control; claude assigns a new UUID and writes it to
        // the per-PID registry alongside the bridgeSessionId we capture below. Resume
        // (sessionId provided): --resume <sid> --remote-control reattaches to an existing
        // conversation. Caveat: claude-code on v2.1.138 prints "No deferred tool marker
        // found …" on resume of a non-deferred session, but the relay still registers and
        // the bridge populates as expected.
        var innerCmd = string.IsNullOrEmpty(sessionId)
            ? $"\"{_options.ClaudePath}\" --remote-control \"{rcName}\""
            : $"\"{_options.ClaudePath}\" --resume {sessionId} --remote-control \"{rcName}\"";

        // cmd.exe's argument-parsing rule for /K (and /C): when the first character after
        // /K is a quote, cmd strips ONE outer pair of quotes. So we wrap the whole inner
        // command in an extra pair, leaving the inner quotes (around ClaudePath and rcName)
        // intact so paths with spaces survive. SafeRcName already strips shell-special
        // characters from the branch, so the rcName interpolation is safe.
        var cmdArgs = $"/K \"{innerCmd}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            WorkingDirectory = branch.Worktree,
            // UseShellExecute=true is what gives cmd.exe its own visible console window.
            // The previous PowerShell-via-Start-Process gymnastics existed because
            // Process.Start with UseShellExecute=false would have made the child share
            // the parent's console (and RC silently fails to register on a shared TTY).
            // Under the WinExe daemon there's no parent console at all, but UseShellExecute
            // remains the correct flag — it asks Windows to allocate a new console for the
            // child regardless of what the daemon's subsystem looks like.
            UseShellExecute = true,
        };

        _logger.LogInformation(
            "spawning RC cmd.exe sessionId={Sid} branch={Branch} worktree={Worktree} rcName={RcName}",
            sessionId ?? "(fresh)", branch.Branch, branch.Worktree, rcName);

        SysProcess? launched;
        try
        {
            launched = SysProcess.Start(psi);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to launch cmd.exe for RC session ({branch.Branch}): {ex.Message}", ex);
        }

        if (launched == null)
        {
            throw new InvalidOperationException(
                $"Process.Start returned null for RC session ({branch.Branch})");
        }

        var psPid = launched.Id;
        _logger.LogDebug("RC cmd.exe PID={PsPid}", psPid);

        var (claudePid, claudeStart) = await FindChildClaudeAsync(psPid, cancellationToken);
        var (bridgeId, capturedSid) = await PollRegistryForBridgeAsync(claudePid, cancellationToken);

        // On a fresh spawn we trust the registry's sessionId; on a resume we keep the value the
        // caller passed (the registry SHOULD agree, but we don't second-guess our own state).
        var resolvedSid = !string.IsNullOrEmpty(sessionId) ? sessionId : capturedSid;
        if (string.IsNullOrEmpty(resolvedSid))
        {
            throw new InvalidOperationException(
                $"could not resolve sessionId after spawn (registry returned empty for PID {claudePid})");
        }

        var url = $"https://claude.ai/code/{bridgeId}";

        WriteDaemonActiveMarker(branch.Worktree, resolvedSid, bridgeId);

        return new RcAttachment(
            PsPid: psPid,
            ClaudePid: claudePid,
            ClaudeStartTicks: claudeStart.Ticks,
            BridgeSessionId: bridgeId,
            Url: url,
            SessionId: resolvedSid);
    }

    public Task CleanupAsync(BranchState rec, CancellationToken cancellationToken)
    {
        if (rec.RcPid is int psPid)
            TryKillProcessTree(psPid);

        if (!string.IsNullOrEmpty(rec.Worktree))
        {
            try
            {
                _fs.DeleteFile(Path.Combine(rec.Worktree, ".daemon-active"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to delete .daemon-active in {Worktree}", rec.Worktree);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<bool> IsAliveAsync(int claudePid, long claudeStartTicks, CancellationToken cancellationToken)
    {
        try
        {
            var p = SysProcess.GetProcessById(claudePid);

            // PID-recycle defense: a freshly-spawned process with the same PID would have a
            // different StartTime.
            if (p.StartTime.Ticks != claudeStartTicks)
            {
                _logger.LogDebug(
                    "IsAlive false: PID {Pid} StartTime ticks {Actual} != recorded {Expected} (PID recycled)",
                    claudePid, p.StartTime.Ticks, claudeStartTicks);
                return false;
            }
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when the PID isn't running.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process exited mid-call.
            return false;
        }

        // Bridge-alive check: the registry must still show a non-null bridgeSessionId. If the
        // relay tore down (e.g. the 10-min outage scenario from recipe.md) the local process can
        // remain alive but we should treat the session as dead and respawn.
        //
        // claude.exe rewrites the registry file in place when bridge state changes. A read
        // landing mid-write produces an IOException or JsonException — without a retry, the
        // dispatcher would reap a healthy RC because we caught the file in flux. One retry
        // after 100ms is enough to clear that window in practice.
        var path = RegistryPath(claudePid);
        if (!_fs.FileExists(path))
            return false;

        if (TryReadRegistry(path, claudePid, "IsAlive").Ok)
            return true;

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        return TryReadRegistry(path, claudePid, "IsAlive").Ok;
    }

    /// <summary>
    /// Reads the per-PID claude registry JSON and extracts <c>bridgeSessionId</c> +
    /// <c>sessionId</c>. <see cref="RegistryRead.Ok"/> is true only when bridgeSessionId is
    /// a non-empty string. Transient IO/Json failures are swallowed (the file is rewritten in
    /// place by claude.exe and reads can land mid-write); callers retry as appropriate.
    /// </summary>
    RegistryRead TryReadRegistry(string path, int claudePid, string context)
    {
        try
        {
            var json = _fs.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("bridgeSessionId", out var b)
                || b.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(b.GetString()))
            {
                return default;
            }

            var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
            return new RegistryRead(true, b.GetString()!, sid);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "{Context}: registry read transient failure for PID {Pid}", context, claudePid);
            return default;
        }
    }

    readonly record struct RegistryRead(bool Ok, string BridgeSessionId, string SessionId);

    // ---------------- helpers ----------------

    async Task<(int Pid, DateTime Start)> FindChildClaudeAsync(int parentPid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ChildLookupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = QueryClaudeChild(parentPid);
            if (found.HasValue)
            {
                _logger.LogDebug("found claude.exe child PID {ClaudePid} of cmd.exe {PsPid}", found.Value.Pid, parentPid);
                return found.Value;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"No claude.exe child of cmd.exe PID {parentPid} appeared within {ChildLookupTimeout.TotalSeconds:N0}s");
    }

    static (int Pid, DateTime Start)? QueryClaudeChild(int parentPid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid} AND Name = 'claude.exe'");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    try
                    {
                        var p = SysProcess.GetProcessById(pid);
                        return (pid, p.StartTime);
                    }
                    catch
                    {
                        // Race: process exited between WMI enumeration and GetProcessById.
                        continue;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // WMI hiccup — try again next tick.
        }
        return null;
    }

    async Task<(string BridgeSessionId, string SessionId)> PollRegistryForBridgeAsync(int claudePid, CancellationToken cancellationToken)
    {
        var path = RegistryPath(claudePid);
        var deadline = DateTime.UtcNow + RegistryPollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_fs.FileExists(path))
            {
                var read = TryReadRegistry(path, claudePid, "PollRegistry");
                if (read.Ok)
                {
                    _logger.LogDebug("registry populated for PID {Pid}: bridge={Bridge} sid={Sid}",
                        claudePid, read.BridgeSessionId, read.SessionId);
                    return (read.BridgeSessionId, read.SessionId);
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"bridgeSessionId not populated within {RegistryPollTimeout.TotalSeconds:N0}s for claude PID {claudePid}");
    }

    void WriteDaemonActiveMarker(string worktree, string sessionId, string bridgeSessionId)
    {
        var markerPath = Path.Combine(worktree, ".daemon-active");
        var json = JsonSerializer.Serialize(new
        {
            sessionId,
            bridgeSessionId,
            ts = DateTimeOffset.UtcNow.ToString("O"),
        });
        try
        {
            _fs.WriteAllText(markerPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to write .daemon-active in {Worktree} — continuing", worktree);
        }
    }

    void TryKillProcessTree(int psPid)
    {
        try
        {
            var p = SysProcess.GetProcessById(psPid);
            p.Kill(entireProcessTree: true);
            _logger.LogDebug("killed cmd.exe process tree PID={Pid}", psPid);
        }
        catch (ArgumentException)
        {
            // Already dead.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to kill cmd.exe PID {Pid}", psPid);
        }
    }

    static string RegistryPath(int claudePid)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "sessions", $"{claudePid}.json");

    /// <summary>
    /// Branch names for fork PRs are attacker-controllable, so we use a strict allowlist
    /// rather than blacklisting metachars. Anything outside [A-Za-z0-9._+/-] becomes '-'.
    /// The set covers every character `git check-ref-format` accepts in a normal branch
    /// name plus '+' which appears in version-style branches; it excludes the shell-special
    /// set that's dangerous in either cmd.exe ('&' '|' '<' '>' '^' '%') or PowerShell
    /// ($ ` ' " ( ) { } and space), so the rcName is safe to interpolate into either.
    /// </summary>
    internal static string SafeRcName(string branch)
    {
        if (string.IsNullOrEmpty(branch))
            return "ai-daemon";

        var sb = new System.Text.StringBuilder(branch.Length);
        foreach (var ch in branch)
        {
            var ok = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                  or '.' or '_' or '+' or '/' or '-';
            sb.Append(ok ? ch : '-');
        }

        var s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "ai-daemon" : s;
    }

}

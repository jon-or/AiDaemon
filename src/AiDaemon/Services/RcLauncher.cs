using System.Diagnostics;
using System.Management;
using System.Text.Json;
using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using AiDaemon.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SysProcess = System.Diagnostics.Process;

namespace AiDaemon.Services;

public class RcLauncher : IRcLauncher
{
    /// <summary>How long to poll WMI looking for the inner <c>claude.exe</c> child of PowerShell.</summary>
    static readonly TimeSpan ChildLookupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long to poll the per-PID registry for <c>bridgeSessionId</c>.</summary>
    static readonly TimeSpan RegistryPollTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the outer Start-Process call to return the inner PS PID.</summary>
    static readonly TimeSpan StartProcessTimeout = TimeSpan.FromSeconds(10);

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly IProcessRunner _runner;
    readonly IFileSystem _fs;
    readonly DaemonOptions _options;
    readonly ILogger<RcLauncher> _logger;

    public RcLauncher(
        IProcessRunner runner,
        IFileSystem fs,
        IOptions<DaemonOptions> options,
        ILogger<RcLauncher> logger)
    {
        _runner = runner;
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RcAttachment> SpawnRcAsync(BranchInfo branch, string? sessionId, CancellationToken cancellationToken)
    {
        var rcName = SafeRcName(branch.Branch);

        // The inner command claude actually runs inside the new console window.
        // Fresh spawn (no sessionId): just --remote-control. claude assigns a new UUID and writes
        // it to the per-PID registry alongside the bridgeSessionId we'll capture below.
        // Resume (sessionId provided): --resume <sid> --remote-control to reattach to an existing
        // conversation. Caveat: claude-code on v2.1.138 prints "No deferred tool marker found …"
        // on resume of a non-deferred session, but the relay still registers and the bridge
        // populates as expected.
        var innerCmd = string.IsNullOrEmpty(sessionId)
            ? $"& \"{_options.ClaudePath}\" --remote-control \"{rcName}\""
            : $"& \"{_options.ClaudePath}\" --resume {sessionId} --remote-control \"{rcName}\"";

        // Outer PowerShell calls Start-Process to give the inner PowerShell its own console
        // window — Process.Start with UseShellExecute=false from a Git Bash / dotnet-run parent
        // would otherwise share the parent's console (and RC silently fails to register on a
        // shared TTY).
        var outerScript = BuildStartProcessScript(branch.Worktree, innerCmd);

        _logger.LogInformation(
            "spawning RC PowerShell sessionId={Sid} branch={Branch} worktree={Worktree} rcName={RcName}",
            sessionId ?? "(fresh)", branch.Branch, branch.Worktree, rcName);

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startCts.CancelAfter(StartProcessTimeout);

        var startResult = await _runner.RunAsync(
            _options.PowerShellPath,
            new[] { "-NoProfile", "-NonInteractive", "-Command", outerScript },
            cancellationToken: startCts.Token);

        if (!startResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Start-Process failed (exit {startResult.ExitCode}): {Truncate(startResult.Stderr, 500)}");
        }

        if (!int.TryParse(startResult.Stdout.Trim(), out var psPid) || psPid <= 0)
        {
            throw new InvalidOperationException(
                $"Start-Process did not return a parseable PID. stdout='{Truncate(startResult.Stdout, 200)}' stderr='{Truncate(startResult.Stderr, 200)}'");
        }

        _logger.LogDebug("inner PowerShell PID={PsPid}", psPid);

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

    public bool IsAlive(int claudePid, long claudeStartTicks)
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
        var path = RegistryPath(claudePid);
        if (!_fs.FileExists(path))
            return false;

        try
        {
            var json = _fs.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("bridgeSessionId", out var b)
                && b.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(b.GetString());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "IsAlive: registry read transient failure for PID {Pid} — treating as dead", claudePid);
            return false;
        }
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Outer-PowerShell script: launch a new visible console with <see cref="ProcessWindowStyle.Normal"/>,
    /// run the supplied <paramref name="innerCmd"/> via <c>-NoExit -Command</c>, return the
    /// inner PowerShell PID on stdout. Uses the same <c>PowerShellPath</c> as the outer call
    /// so a custom config (e.g. pwsh.exe) is honored on both sides. Single-quotes around
    /// interpolated paths/cmd are PowerShell single-quoted-string literals; we double any
    /// embedded apostrophe to escape it.
    /// </summary>
    string BuildStartProcessScript(string worktree, string innerCmd)
    {
        static string EscapeSingle(string s) => s.Replace("'", "''");
        var ps = EscapeSingle(_options.PowerShellPath);
        var wd = EscapeSingle(worktree);
        var cmd = EscapeSingle(innerCmd);

        return
            $"$ErrorActionPreference='Stop'; " +
            $"$p = Start-Process -FilePath '{ps}' -PassThru " +
            $"-WorkingDirectory '{wd}' " +
            $"-ArgumentList @('-NoExit','-NoProfile','-Command','{cmd}'); " +
            $"if ($p) {{ Write-Output $p.Id }} else {{ Write-Error 'Start-Process returned null' }}";
    }

    async Task<(int Pid, DateTime Start)> FindChildClaudeAsync(int parentPid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ChildLookupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = QueryClaudeChild(parentPid);
            if (found.HasValue)
            {
                _logger.LogDebug("found claude.exe child PID {ClaudePid} of PowerShell {PsPid}", found.Value.Pid, parentPid);
                return found.Value;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"No claude.exe child of PowerShell PID {parentPid} appeared within {ChildLookupTimeout.TotalSeconds:N0}s");
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
                try
                {
                    var json = _fs.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("bridgeSessionId", out var b)
                        && b.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(b.GetString()))
                    {
                        var bsid = b.GetString()!;
                        var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
                        _logger.LogDebug("registry populated for PID {Pid}: bridge={Bridge} sid={Sid}", claudePid, bsid, sid);
                        return (bsid, sid);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "registry read transient failure for PID {Pid} — retrying", claudePid);
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
            _logger.LogDebug("killed PowerShell process tree PID={Pid}", psPid);
        }
        catch (ArgumentException)
        {
            // Already dead.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to kill PowerShell PID {Pid}", psPid);
        }
    }

    static string RegistryPath(int claudePid)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "sessions", $"{claudePid}.json");

    /// <summary>
    /// PowerShell + the relay tolerate most strings, but quotes/backticks would break the
    /// command string we hand to <c>-Command</c>. Strip them.
    /// </summary>
    static string SafeRcName(string branch)
    {
        var s = branch.Replace("\"", "").Replace("`", "").Replace("$", "").Replace("'", "").Trim();
        return string.IsNullOrEmpty(s) ? "ai-daemon" : s;
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class Dispatcher : IDispatcher
{
    readonly IRcLauncher _launcher;
    readonly INotificationPusher _pusher;
    readonly IStateStore _store;
    readonly IFileSystem _fs;
    readonly DaemonOptions _options;
    readonly ILogger<Dispatcher> _logger;

    public Dispatcher(
        IRcLauncher launcher,
        INotificationPusher pusher,
        IStateStore store,
        IFileSystem fs,
        IOptions<DaemonOptions> options,
        ILogger<Dispatcher> logger)
    {
        _launcher = launcher;
        _pusher = pusher;
        _store = store;
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DispatchOutcome> DispatchAsync(
        BranchInfo branch, GhNotification notification, TriageVerdict verdict, CancellationToken cancellationToken)
    {
        var key = branch.Key;
        var rec = await _store.GetBranchStateAsync(key, cancellationToken)
            ?? new BranchState
            {
                Branch = key,
                Worktree = branch.Worktree,
                Mode = BranchMode.Idle,
                LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                PrNumber = branch.PrNumber,
                IssueNumber = branch.IssueNumber,
            };

        // Branch may have moved since the row was last written (e.g. force-push then PR re-opened
        // on a different worktree). Refresh to the current resolution.
        rec.Worktree = branch.Worktree;
        if (branch.PrNumber != null) rec.PrNumber = branch.PrNumber;
        if (branch.IssueNumber != null) rec.IssueNumber = branch.IssueNumber;

        // Case 1: alive — heads-up only.
        if (rec.Mode == BranchMode.RcActive
            && rec.RcClaudePid is int claudePid
            && rec.RcClaudeStart is long claudeStart
            && _launcher.IsAlive(claudePid, claudeStart))
        {
            rec.LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _store.UpsertBranchStateAsync(rec, cancellationToken);
            await _pusher.PushHeadsUpAsync(rec.RcUrl ?? "", branch, notification, verdict, cancellationToken);
            _logger.LogInformation(
                "dispatch=heads_up branch={Branch} url={Url}", key, rec.RcUrl);
            return DispatchOutcome.HeadsUp;
        }

        // Case 2: row says active but the process is gone or the bridge tore down. Reap, then
        // fall through to the spawn path.
        if (rec.Mode == BranchMode.RcActive)
        {
            _logger.LogInformation(
                "dispatch: stale RcActive for branch={Branch} (pid={Pid}) — cleaning up before respawn",
                key, rec.RcClaudePid);
            try
            {
                await _launcher.CleanupAsync(rec, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cleanup threw — proceeding with respawn");
            }
            ResetRcFields(rec);
        }

        // Case 3: spawn from Idle. First spawn passes null sessionId (claude assigns a fresh
        // UUID and we capture it from the registry). Respawn after a previous death passes the
        // sessionId we recorded so the conversation history is preserved.
        var seedSid = string.IsNullOrEmpty(rec.SessionId) ? null : rec.SessionId;

        RcAttachment attachment;
        try
        {
            attachment = await _launcher.SpawnRcAsync(branch, seedSid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "spawn failed branch={Branch} sid={Sid} — aborting dispatch", key, seedSid ?? "(fresh)");
            // Persist whatever state we have so the next attempt resumes correctly.
            await _store.UpsertBranchStateAsync(rec, cancellationToken);
            return DispatchOutcome.Failed;
        }

        rec.SessionId = attachment.SessionId;
        rec.Mode = BranchMode.RcActive;
        rec.RcPid = attachment.PsPid;
        rec.RcClaudePid = attachment.ClaudePid;
        rec.RcClaudeStart = attachment.ClaudeStartTicks;
        rec.RcBridgeId = attachment.BridgeSessionId;
        rec.RcUrl = attachment.Url;
        rec.LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _store.UpsertBranchStateAsync(rec, cancellationToken);
        await _pusher.PushSessionLinkAsync(attachment.Url, branch, notification, verdict, cancellationToken);

        _logger.LogInformation(
            "dispatch=spawned branch={Branch} sid={Sid} bridge={Bridge} url={Url}",
            key, rec.SessionId, attachment.BridgeSessionId, attachment.Url);

        return DispatchOutcome.Spawned;
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var rows = await _store.ListActiveBranchesAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        var idleTimeout = TimeSpan.FromHours(Math.Max(1, _options.RcIdleTimeoutHours));
        var idleThreshold = DateTime.UtcNow - idleTimeout;

        foreach (var rec in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Case 1: dead process or torn-down bridge.
            if (rec.RcClaudePid is not int pid
                || rec.RcClaudeStart is not long start
                || !_launcher.IsAlive(pid, start))
            {
                _logger.LogInformation("sweep: reaping dead/stale RC branch={Branch} pid={Pid}", rec.Branch, rec.RcClaudePid);
                await ReapAsync(rec, "dead", cancellationToken);
                continue;
            }

            // Case 2: idle timeout — JSONL untouched for > RcIdleTimeoutHours.
            if (TryGetJsonlPath(rec, out var jsonlPath)
                && _fs.FileExists(jsonlPath)
                && _fs.GetLastWriteTimeUtc(jsonlPath) < idleThreshold)
            {
                _logger.LogInformation(
                    "sweep: reaping idle RC branch={Branch} jsonl_mtime={Mtime:O} (>{Hours}h since last activity)",
                    rec.Branch, _fs.GetLastWriteTimeUtc(jsonlPath), idleTimeout.TotalHours);
                await ReapAsync(rec, "idle-timeout", cancellationToken);
            }
        }
    }

    public async Task ReconcileOnStartupAsync(CancellationToken cancellationToken)
    {
        var rows = await _store.ListActiveBranchesAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        _logger.LogInformation("startup reconciliation: {Count} RcActive rows from before shutdown — resetting", rows.Count);
        foreach (var rec in rows)
        {
            // Don't run CleanupAsync — the OS process is gone. Just clear the marker file and reset.
            try
            {
                if (!string.IsNullOrEmpty(rec.Worktree))
                    _fs.DeleteFile(Path.Combine(rec.Worktree, ".daemon-active"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "marker cleanup on startup for {Worktree} failed (ignored)", rec.Worktree);
            }

            ResetRcFields(rec);
            await _store.UpsertBranchStateAsync(rec, cancellationToken);
        }
    }

    async Task ReapAsync(BranchState rec, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _launcher.CleanupAsync(rec, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sweep cleanup threw for branch={Branch} reason={Reason}", rec.Branch, reason);
        }

        ResetRcFields(rec);
        await _store.UpsertBranchStateAsync(rec, cancellationToken);
    }

    static void ResetRcFields(BranchState rec)
    {
        rec.Mode = BranchMode.Idle;
        rec.RcPid = null;
        rec.RcClaudePid = null;
        rec.RcClaudeStart = null;
        rec.RcBridgeId = null;
        rec.RcUrl = null;
        // SessionId is preserved deliberately — next event resumes the same conversation.
    }

    bool TryGetJsonlPath(BranchState rec, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(rec.SessionId) || string.IsNullOrEmpty(rec.Worktree))
            return false;

        // ~/.claude/projects/<encoded-cwd>/<sid>.jsonl, where <encoded-cwd> is the worktree path
        // with each path separator (and the drive colon on Windows) replaced by '-'.
        var encoded = rec.Worktree
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace(":", "-");

        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects",
            encoded,
            $"{rec.SessionId}.jsonl");

        return true;
    }

}

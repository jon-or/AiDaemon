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
    readonly IAgentPreRunner _preRunner;
    readonly INotificationPusher _pusher;
    readonly IStateStore _store;
    readonly IFileSystem _fs;
    readonly DaemonOptions _options;
    readonly ILogger<Dispatcher> _logger;

    public Dispatcher(
        IRcLauncher launcher,
        IAgentPreRunner preRunner,
        INotificationPusher pusher,
        IStateStore store,
        IFileSystem fs,
        IOptions<DaemonOptions> options,
        ILogger<Dispatcher> logger)
    {
        _launcher = launcher;
        _preRunner = preRunner;
        _pusher = pusher;
        _store = store;
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DispatchOutcome> DispatchAsync(
        BranchInfo branch, IReadOnlyList<NotificationWithBody> items, TriageVerdict verdict, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            throw new ArgumentException("Dispatch requires at least one notification.", nameof(items));

        // Use the most-recent notification for any "primary" metadata (push title, etc.).
        var primary = items.OrderByDescending(i => i.Notification.UpdatedAt).First().Notification;

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
            await _pusher.PushHeadsUpAsync(rec.RcUrl ?? "", branch, primary, verdict, cancellationToken);
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

        // Case 3: spawn from Idle. Three options for the session id we hand RC:
        //   * Cross-tick resume — if we already have a session for this branch from a prior
        //     dispatch, reuse it so the conversation history is preserved.
        //   * Fresh pre-run — generate a new session id, run the headless pre-run agent in the
        //     worktree to do the research/fix work, then RC resumes that session.
        var seedSid = string.IsNullOrEmpty(rec.SessionId) ? null : rec.SessionId;

        if (seedSid == null)
        {
            // No prior session for this branch — generate a sid and run the pre-run agent
            // against it before opening RC. The pre-run is what actually does the work; RC
            // is just the user's window into the resulting transcript.
            seedSid = Guid.NewGuid().ToString();

            try
            {
                await _preRunner.RunAsync(seedSid, branch, items, verdict, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "pre-run threw — proceeding to RC anyway sid={Sid} branch={Branch}",
                    seedSid, key);
            }
        }
        else
        {
            _logger.LogInformation(
                "dispatch: reusing prior session sid={Sid} branch={Branch} — skipping pre-run",
                seedSid, key);
        }

        RcAttachment attachment;
        try
        {
            attachment = await _launcher.SpawnRcAsync(branch, seedSid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "spawn failed branch={Branch} sid={Sid} — aborting dispatch", key, seedSid ?? "(fresh)");
            // Persist whatever state we have so the next attempt resumes correctly.
            rec.SessionId = seedSid ?? rec.SessionId;
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
        await _pusher.PushSessionLinkAsync(attachment.Url, branch, primary, verdict, cancellationToken);

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

        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects",
            EncodeWorktreeAsProjectDir(rec.Worktree),
            $"{rec.SessionId}.jsonl");

        return true;
    }

    /// <summary>
    /// Converts a worktree path to the encoded directory name claude uses under
    /// <c>~/.claude/projects/</c>. The encoding replaces every non-alphanumeric character
    /// (and non-hyphen) with <c>-</c>, then lowercases the result. Recipe.md mentioned only
    /// path separators + drive colon, but empirically dots in directory names (e.g.
    /// <c>orez.worktrees</c>) and other punctuation are also replaced — get this wrong and
    /// the idle-timeout sweep can never find the JSONL.
    /// </summary>
    public static string EncodeWorktreeAsProjectDir(string worktreePath)
    {
        var sb = new System.Text.StringBuilder(worktreePath.Length);
        foreach (var ch in worktreePath)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
                sb.Append(char.ToLowerInvariant(ch));
            else
                sb.Append('-');
        }
        return sb.ToString();
    }
}

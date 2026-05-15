using System.Text;
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

        // Most-recent notification's subject title — what the conversation is "about" on
        // GitHub. Surfaces as the ntfy push title so the user reads the issue/PR name
        // rather than the branch slug.
        var subjectTitle = items.OrderByDescending(i => i.Notification.UpdatedAt)
            .First().Notification.Subject.Title;

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
            && await _launcher.IsAliveAsync(claudePid, claudeStart, cancellationToken))
        {
            rec.LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _store.UpsertBranchStateAsync(rec, cancellationToken);
            await _pusher.PushHeadsUpAsync(rec.RcUrl ?? "", branch, subjectTitle, verdict, cancellationToken);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "cleanup threw — proceeding with respawn");
            }
            ResetRcFields(rec);
        }

        // Case 3: spawn from Idle. Two sub-cases for the sid we hand pre-run + RC:
        //   * Fresh: no prior session for this branch — generate a sid, run pre-run against
        //     it, then RC resumes that session.
        //   * Cross-tick resume: rec.SessionId carries over from a prior dispatch. Run
        //     pre-run against THAT sid so the new comments' research/fix work appends to
        //     the existing JSONL — when the user opens RC they see the prior conversation
        //     plus the new turns. Without this, the user would just see a stale transcript
        //     with no awareness of the comment that fired this dispatch.
        var freshSid = string.IsNullOrEmpty(rec.SessionId);
        string? seedSid;

        if (freshSid)
        {
            seedSid = Guid.NewGuid().ToString();

            // Persist the sid BEFORE the pre-run. The pre-run's wall-clock budget is 10
            // minutes and we may edit files in the worktree along the way. If the daemon
            // is killed mid-pre-run, we don't want the next dispatch to generate a brand
            // new sid and re-run pre-run on the same worktree from scratch (duplicate
            // edits, lost transcript continuity). With the sid persisted, a mid-pre-run
            // crash → next dispatch sees rec.SessionId set → re-runs pre-run on the SAME
            // sid (the cross-tick path below), which appends rather than restarts.
            rec.SessionId = seedSid;
            await _store.UpsertBranchStateAsync(rec, cancellationToken);
        }
        else
        {
            seedSid = rec.SessionId;
            _logger.LogInformation(
                "dispatch: resuming prior session sid={Sid} branch={Branch} — pre-run will append new turns",
                seedSid, key);
        }

        var preRunSummary = "";
        try
        {
            var preRun = await _preRunner.RunAsync(seedSid!, branch, items, verdict, cancellationToken);
            preRunSummary = preRun.Summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "pre-run threw — proceeding to RC anyway sid={Sid} branch={Branch}",
                seedSid, key);
        }

        // Empty-JSONL fallback only applies to a fresh sid. If the pre-run died before
        // writing any turn (network blip on the first response, hard timeout, or process
        // crash) the JSONL claude expects for `--resume <sid>` doesn't exist, and the RC
        // window opens to a "No conversation found." message. Detect that and downgrade
        // to a fresh spawn — the launcher will assign a new sid and we overwrite
        // rec.SessionId after.
        //
        // For a cross-tick resume the JSONL is guaranteed to exist (the prior pre-run
        // wrote it); even if the current pre-run did nothing, --resume still works against
        // the prior turns, so we skip this fallback.
        if (freshSid && TryGetJsonlPath(rec, out var jsonlPath) && !_fs.FileExists(jsonlPath))
        {
            _logger.LogWarning(
                "pre-run wrote no JSONL for sid={Sid} branch={Branch} — falling back to fresh RC spawn",
                seedSid, key);
            seedSid = null;
            rec.SessionId = "";
            // Don't persist here: SpawnRcAsync's success path overwrites rec.SessionId
            // with the launcher-assigned value; if spawn itself fails, the catch below
            // re-persists whatever sid we still have.
        }

        // The pre-run's summary credits the requester by name and describes both ask + work
        // done; prefer it over the triage verdict's terser one-liner whenever it landed.
        // Cross-tick resume / heads-up paths skip pre-run, so verdict.Summary stands.
        var pushVerdict = string.IsNullOrEmpty(preRunSummary)
            ? verdict
            : verdict with { Summary = preRunSummary };

        RcAttachment attachment;
        try
        {
            attachment = await _launcher.SpawnRcAsync(branch, seedSid, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "spawn failed branch={Branch} sid={Sid} — RC unavailable", key, seedSid);
            // seedSid is already persisted on rec.SessionId (cross-tick reuse path: it was
            // there when we entered; first-spawn path: we wrote it before pre-run). The
            // empty-JSONL fallback above intentionally clears it so the next dispatch
            // generates a brand-new sid and re-runs pre-run.
            rec.SessionId = seedSid ?? "";
            await _store.UpsertBranchStateAsync(rec, cancellationToken);

            // Push anyway — the user still needs to know an actionable thing arrived even if
            // the RC relay is down. Same shape as a normal session-link push, but with
            // "Not Available" in the URL slot. Use pushVerdict so the pre-run's summary
            // (if it produced one before the spawn failed) still surfaces on the phone.
            await _pusher.PushSessionLinkAsync("Not Available", branch, subjectTitle, pushVerdict, cancellationToken);

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
        await _pusher.PushSessionLinkAsync(attachment.Url, branch, subjectTitle, pushVerdict, cancellationToken);

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
                || !await _launcher.IsAliveAsync(pid, start, cancellationToken))
            {
                _logger.LogInformation("sweep: reaping dead/stale RC branch={Branch} pid={Pid}", rec.Branch, rec.RcClaudePid);
                await ReapAsync(rec, "dead", cancellationToken);
                continue;
            }

            // Case 2: idle timeout — JSONL untouched for > RcIdleTimeoutHours.
            // Skip if the JSONL is missing (fresh spawn that hasn't flushed yet) — File's
            // sentinel for that is 1601-01-01 UTC, well before any reasonable idleThreshold,
            // and we don't want to reap a session that simply hasn't written its first turn.
            if (TryGetJsonlPath(rec, out var jsonlPath) && _fs.FileExists(jsonlPath))
            {
                var mtime = _fs.GetLastWriteTimeUtc(jsonlPath);
                if (mtime < idleThreshold)
                {
                    _logger.LogInformation(
                        "sweep: reaping idle RC branch={Branch} jsonl_mtime={Mtime:O} (>{Hours}h since last activity)",
                        rec.Branch, mtime, idleTimeout.TotalHours);
                    await ReapAsync(rec, "idle-timeout", cancellationToken);
                }
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// (and non-hyphen) with <c>-</c> and preserves case. Recipe.md mentioned only path
    /// separators + drive colon, but empirically dots in directory names (e.g.
    /// <c>orez.worktrees</c>) and other punctuation are also replaced — and case is
    /// preserved as the cwd was when claude was launched, so a worktree of <c>D:\...</c>
    /// produces <c>D--...</c> and <c>d:\...</c> produces <c>d--...</c>. NTFS hides the
    /// difference on <c>File.Exists</c>, but the idle-timeout sweep relies on string
    /// equality of the path it computes here matching what claude wrote on disk.
    /// </summary>
    internal static string EncodeWorktreeAsProjectDir(string worktreePath)
    {
        var sb = new StringBuilder(worktreePath.Length);
        foreach (var ch in worktreePath)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
                sb.Append(ch);
            else
                sb.Append('-');
        }
        return sb.ToString();
    }
}

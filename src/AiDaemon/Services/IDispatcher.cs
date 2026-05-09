using AiDaemon.Models;

namespace AiDaemon.Services;

public interface IDispatcher
{
    /// <summary>
    /// Routes an actionable verdict to either a fresh RC spawn (with session-link push) or a
    /// heads-up push against an existing live session. Idempotent across ticks via the branches
    /// state table; the within-tick coalescing happens in the worker.
    /// </summary>
    Task<DispatchOutcome> DispatchAsync(
        BranchInfo branch,
        GhNotification notification,
        TriageVerdict verdict,
        CancellationToken cancellationToken);

    /// <summary>
    /// Walk every <c>RcActive</c> branch and reap any whose process has died, whose bridge has
    /// torn down, or whose conversation has been idle for <c>RcIdleTimeoutHours</c>. The
    /// session_id is preserved so the next event respawns into the same conversation.
    /// </summary>
    Task SweepAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One-time reconciliation on daemon startup: every PID we recorded before the last shutdown
    /// is dead by definition, so this walks every <c>RcActive</c> row and resets it to <c>Idle</c>
    /// without trying to clean up the (already-gone) process tree.
    /// </summary>
    Task ReconcileOnStartupAsync(CancellationToken cancellationToken);
}

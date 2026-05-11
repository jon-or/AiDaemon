using AiDaemon.Models;

namespace AiDaemon.Services;

/// <summary>
/// Runs a single notification end-to-end through the L1/L2/L3 + dispatch pipeline. The poll
/// path inside <see cref="Worker.TickAsync"/> still does its own pass-1/pass-2 streaming for
/// throughput, but the tray-icon Retry path needs to fire just one notification in isolation
/// (and skip the per-tick metrics + rate-limit charge that wouldn't make sense for a user-
/// initiated retry). Sharing the pipeline mechanics across both call sites is achieved by
/// re-using the same <c>ITriagePipeline</c>, <c>IBranchResolver</c>, and <c>IDispatcher</c>
/// services here; only the orchestration shape differs.
/// </summary>
public interface INotificationProcessor
{
    /// <summary>
    /// Run a single notification through quick triage, branch resolve, agent triage, and
    /// dispatch. Marks the row processed at every termination point with the same outcome
    /// vocabulary the poll path uses (<c>dropped:</c>, <c>unresolved</c>, <c>spawned:</c>,
    /// <c>heads-up:</c>, <c>failed:*</c>). Rate-limit budget is NOT charged for retries.
    /// </summary>
    Task<RetryOutcome> ProcessOneAsync(GhNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Coarse-grained result for the tray UI. Maps to the same outcome the poll path would have
/// recorded, but flattened to a single enum so the tray can surface a balloon ("Spawned",
/// "Dropped: noise", etc.) without parsing the outcome string.
/// </summary>
public enum RetryOutcome
{
    Dropped,
    Unresolved,
    Spawned,
    HeadsUp,
    Failed,
}

using AiDaemon.Models;

namespace AiDaemon.Services;

public interface ITriagePipeline
{
    /// <summary>
    /// L1 (author/type/reason/rate-limit) and L2 (regex content) only — cheap deterministic
    /// filters. Returns the resulting verdict plus the comment body we fetched at L1 time
    /// (carried so downstream callers don't re-fetch).
    /// <para>
    /// <c>Verdict</c> is non-null when L1 or L2 reaches a definitive decision (drop or
    /// short-circuit actionable). It is <c>null</c> when the notification needs the agent
    /// triage (<see cref="AgentTriageAsync"/>) to make the call.
    /// </para>
    /// Read-only with respect to the rate-limit table — drops when the per-thread daily
    /// counter is already at or above the cap. The increment is the worker's responsibility,
    /// applied only after a successful dispatch so dropped notifications don't burn budget.
    /// </summary>
    Task<(TriageVerdict? Verdict, string CommentBody)> QuickTriageAsync(
        GhNotification notification,
        CancellationToken cancellationToken);

    /// <summary>
    /// L3 — runs claude headlessly in the daemon's scratch dir as a pure classifier (no tools,
    /// no session persistence) over <paramref name="items"/>, which may include multiple
    /// notifications that resolved to the same branch in this poll. The classifier sees every
    /// notification's metadata and pre-fetched comment body inline, so a related issue mention
    /// + PR review on the same branch are weighed together as a single decision.
    /// </summary>
    Task<TriageVerdict> AgentTriageAsync(
        IReadOnlyList<NotificationWithBody> items,
        BranchInfo branch,
        CancellationToken cancellationToken);
}

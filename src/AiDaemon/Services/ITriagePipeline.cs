using AiDaemon.Models;

namespace AiDaemon.Services;

public interface ITriagePipeline
{
    /// <summary>
    /// L1 (author/type/reason/rate-limit) and L2 (regex content) only — cheap deterministic
    /// filters. Returns:
    /// <list type="bullet">
    ///   <item><description>A <see cref="TriageVerdict"/> when L1 or L2 reaches a definitive
    ///     decision (drop or short-circuit actionable).</description></item>
    ///   <item><description><c>null</c> when the notification needs the agent triage
    ///     (<see cref="AgentTriageAsync"/>) to make the call.</description></item>
    /// </list>
    /// Side effect: increments the per-thread daily rate-limit counter when the pipeline
    /// reaches the count check.
    /// </summary>
    Task<TriageVerdict?> QuickTriageAsync(GhNotification notification, CancellationToken cancellationToken);

    /// <summary>
    /// L3 — runs claude headlessly inside <paramref name="branch"/>'s worktree with a
    /// daemon-controlled <c>--session-id</c>. The agent decides actionable yes/no AND, when
    /// actionable, performs the initial research / fix work using its tools. The session
    /// transcript is what the user inherits when the dispatcher resumes the same session into
    /// Remote Control. The returned verdict has <c>SessionId</c> set so the dispatcher can
    /// resume the same conversation.
    /// </summary>
    Task<TriageVerdict> AgentTriageAsync(GhNotification notification, BranchInfo branch, CancellationToken cancellationToken);
}

using AiDaemon.Models;

namespace AiDaemon.Services;

public interface IAgentPreRunner
{
    /// <summary>
    /// Runs the headless pre-run agent in <paramref name="branch"/>.Worktree against the
    /// supplied <paramref name="sessionId"/>. The agent has full tool access and is expected
    /// to do the actual research / fix work the triage classified as actionable. When this
    /// returns successfully, the session JSONL contains the agent's transcript and is ready
    /// to be resumed under Remote Control by the user.
    /// </summary>
    /// <param name="items">All notifications that resolved to this branch in the current poll
    /// (e.g. an issue mention + a related PR review). The agent sees every one in its user
    /// message so it has full context for the prep work.</param>
    /// <returns>
    /// True on success (session ready to resume), false if the run failed and the caller
    /// should still spawn RC (the user inherits whatever partial progress made it to disk).
    /// </returns>
    Task<bool> RunAsync(
        string sessionId,
        BranchInfo branch,
        IReadOnlyList<NotificationWithBody> items,
        TriageVerdict verdict,
        CancellationToken cancellationToken);
}

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
    /// <returns>
    /// True on success (session ready to resume), false if the run failed and the caller
    /// should still spawn RC (the user inherits whatever partial progress made it to disk).
    /// </returns>
    Task<bool> RunAsync(
        string sessionId,
        BranchInfo branch,
        GhNotification notification,
        TriageVerdict verdict,
        CancellationToken cancellationToken);
}

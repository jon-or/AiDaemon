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
    /// message — including the comment author login — so it has full context for the prep
    /// work and can credit the requester by name in its summary.</param>
    Task<PreRunResult> RunAsync(
        string sessionId,
        BranchInfo branch,
        IReadOnlyList<NotificationWithBody> items,
        TriageVerdict verdict,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of a pre-run invocation.
/// </summary>
/// <param name="Succeeded">True when the agent completed without is_error and produced a
/// non-empty summary. False on timeout, is_error, or unparseable structured output — the
/// caller should still spawn RC (the user inherits whatever partial progress made it to
/// disk) but should fall back to the triage verdict's summary in the push.</param>
/// <param name="Summary">1-2 sentence summary the agent produced. Empty when
/// <see cref="Succeeded"/> is false.</param>
public record PreRunResult(bool Succeeded, string Summary)
{
    public static PreRunResult Failed { get; } = new(false, "");
}

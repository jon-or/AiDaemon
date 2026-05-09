using AiDaemon.Models;

namespace AiDaemon.Services;

public interface ITriagePipeline
{
    /// <summary>
    /// Runs L1 (author/type/rate-limit) → L2 (regex content) → L3 (Haiku LLM with asymmetric bias)
    /// against <paramref name="notification"/> and returns a verdict.
    ///
    /// Side effect: increments the per-thread daily rate-limit counter via the state store
    /// when L1 reaches the count check.
    /// </summary>
    Task<TriageVerdict> TriageAsync(GhNotification notification, CancellationToken cancellationToken);
}

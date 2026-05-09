using AiDaemon.Models;

namespace AiDaemon.Storage;

public interface IStateStore
{
    /// <summary>Apply schema migrations. Idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> IsProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken);

    Task MarkProcessedAsync(string threadId, string commentId, string outcome, CancellationToken cancellationToken);

    /// <summary>Delete processed rows older than <paramref name="cutoff"/>. Returns rows pruned.</summary>
    Task<int> PruneProcessedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task<BranchState?> GetBranchStateAsync(string branch, CancellationToken cancellationToken);

    Task UpsertBranchStateAsync(BranchState state, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchState>> ListActiveBranchesAsync(CancellationToken cancellationToken);

    /// <summary>Atomically increments and returns the new count for (threadId, today).</summary>
    Task<int> IncrementRateLimitAsync(string threadId, DateOnly day, CancellationToken cancellationToken);

    Task<int> GetRateLimitAsync(string threadId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Read a singleton value from the kv table. Returns null if unset.</summary>
    Task<string?> GetKvAsync(string key, CancellationToken cancellationToken);

    /// <summary>Write a singleton value to the kv table.</summary>
    Task SetKvAsync(string key, string value, CancellationToken cancellationToken);
}

public static class StateStoreKeys
{
    /// <summary>ISO 8601 timestamp of the most recent notification updated_at we've polled past.</summary>
    public const string NotificationCursor = "notifications.cursor";
}

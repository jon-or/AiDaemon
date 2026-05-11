using AiDaemon.Models;

namespace AiDaemon.Storage;

public interface IStateStore
{
    /// <summary>Apply schema migrations. Idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> IsProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken);

    /// <summary>
    /// Record that a notification has been processed. <paramref name="context"/> carries the
    /// display labels (repo, title, subject type) the tray Retry submenu uses; pass null only
    /// when the call site genuinely has no notification on hand (none do today).
    /// </summary>
    Task MarkProcessedAsync(string threadId, string commentId, string outcome, ProcessedContext? context, CancellationToken cancellationToken);

    /// <summary>Delete processed rows older than <paramref name="cutoff"/>. Returns rows pruned.</summary>
    Task<int> PruneProcessedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Return the N most-recently-processed entries, newest first. Used by the tray Retry submenu.</summary>
    Task<IReadOnlyList<ProcessedEntry>> ListRecentProcessedAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Delete the dedup row for (threadId, commentId). The next pipeline run against that
    /// notification will see it as un-processed and proceed normally. Returns true if a row
    /// was deleted.
    /// </summary>
    Task<bool> UnmarkProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken);

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

    /// <summary>ISO 8601 timestamp of the last successful PruneProcessedAsync. Gates the daily run.</summary>
    public const string ProcessedLastPruned = "processed.last_pruned_utc";
}

/// <summary>Display context carried alongside a processed-row write. Pulled from the GhNotification at the call site.</summary>
public sealed record ProcessedContext(string Repo, string Title, string SubjectType)
{
    public static ProcessedContext From(GhNotification n)
        => new(n.Repository.FullName, n.Subject.Title, n.Subject.Type);
}

/// <summary>One row from the processed table, hydrated for the tray Retry submenu.</summary>
public sealed record ProcessedEntry(
    string ThreadId,
    string CommentId,
    DateTimeOffset ProcessedAt,
    string Outcome,
    string? Repo,
    string? Title,
    string? SubjectType);

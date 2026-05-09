using System.Globalization;
using System.Runtime.CompilerServices;
using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging;

namespace AiDaemon.Services;

public class NotificationPoller : INotificationPoller
{
    static readonly string[] CursorFormats =
    {
        "yyyy-MM-ddTHH:mm:ss.fffffffzzz",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ssZ",
    };

    readonly IGhClient _gh;
    readonly IStateStore _store;
    readonly ILogger<NotificationPoller> _logger;

    public NotificationPoller(IGhClient gh, IStateStore store, ILogger<NotificationPoller> logger)
    {
        _gh = gh;
        _store = store;
        _logger = logger;
    }

    public async IAsyncEnumerable<GhNotification> PollAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = await ReadCursorAsync(cancellationToken);

        // First-run safety net: if the cursor is unset, anchor at "now" so we don't pull the
        // entire history of every thread the user has ever participated in.
        if (cursor == null)
        {
            cursor = DateTimeOffset.UtcNow;
            await WriteCursorAsync(cursor.Value, cancellationToken);
            _logger.LogInformation("notification cursor initialized to {Cursor:O}", cursor);
        }

        IReadOnlyList<GhNotification> notifications;
        try
        {
            notifications = await _gh.ListNotificationsAsync(cursor, cancellationToken);
        }
        catch (GhAuthException ex)
        {
            _logger.LogWarning(ex, "Skipping poll due to gh auth failure");
            yield break;
        }

        _logger.LogDebug("polled count={Count} since={Since:O}", notifications.Count, cursor);

        DateTimeOffset? maxSeen = null;

        foreach (var n in notifications)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (maxSeen == null || n.UpdatedAt > maxSeen)
                maxSeen = n.UpdatedAt;

            var commentId = DeriveCommentId(n);

            if (await _store.IsProcessedAsync(n.Id, commentId, cancellationToken))
            {
                _logger.LogDebug(
                    "skip already-processed thread={ThreadId} comment={CommentId} reason={Reason}",
                    n.Id, commentId, n.Reason);
                continue;
            }

            yield return n;
        }

        if (maxSeen.HasValue && maxSeen > cursor)
        {
            // Advance one second past the latest seen so the next `since` query (which is
            // exclusive in practice but we treat conservatively) won't refetch the boundary item.
            var next = maxSeen.Value.AddSeconds(1);
            await WriteCursorAsync(next, cancellationToken);
            _logger.LogDebug("notification cursor advanced to {Cursor:O}", next);
        }
    }

    /// <summary>
    /// Dedup key per notification "tick". If GitHub bumps the same thread (e.g. another comment),
    /// the comment-URL last segment changes and the notification re-fires. For events without a
    /// comment URL (review_requested, etc.) the unread reason is the thread itself, so we key on
    /// updated_at so a re-bump is treated as a fresh event.
    /// </summary>
    public static string DeriveCommentId(GhNotification n)
    {
        var url = n.Subject.LatestCommentUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var slash = url.LastIndexOf('/');
            if (slash >= 0 && slash < url.Length - 1)
                return url[(slash + 1)..];
        }

        return $"updated:{n.UpdatedAt.ToUnixTimeSeconds()}";
    }

    async Task<DateTimeOffset?> ReadCursorAsync(CancellationToken cancellationToken)
    {
        var raw = await _store.GetKvAsync(StateStoreKeys.NotificationCursor, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTimeOffset.TryParseExact(raw, CursorFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            return parsed;

        _logger.LogWarning("notification cursor unparseable: {Raw} — re-anchoring", raw);
        return null;
    }

    Task WriteCursorAsync(DateTimeOffset value, CancellationToken cancellationToken)
        => _store.SetKvAsync(
            StateStoreKeys.NotificationCursor,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);
}

using System.Runtime.CompilerServices;
using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging;

namespace AiDaemon.Services;

public class NotificationPoller : INotificationPoller
{
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
        IReadOnlyList<GhNotification> notifications;
        try
        {
            notifications = await _gh.ListNotificationsAsync(cancellationToken);
        }
        catch (GhAuthException ex)
        {
            // Auth failures are surfaced upstream by GhClient (logged at Error). Ntfy push will be
            // wired in Phase 5; for now, re-throw and let the worker's catch swallow this tick.
            _logger.LogWarning(ex, "Skipping poll due to gh auth failure");
            yield break;
        }

        _logger.LogDebug("polled count={Count}", notifications.Count);

        foreach (var n in notifications)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commentId = DeriveCommentId(n);
            var seen = await _store.IsProcessedAsync(n.Id, commentId, cancellationToken);

            if (seen)
            {
                _logger.LogDebug(
                    "skip already-processed thread={ThreadId} comment={CommentId} reason={Reason}",
                    n.Id, commentId, n.Reason);
                continue;
            }

            yield return n;
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
}

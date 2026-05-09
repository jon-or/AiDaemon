using AiDaemon.Models;

namespace AiDaemon.Services;

public interface INotificationPoller
{
    /// <summary>
    /// Yields notifications that haven't yet been marked processed.
    /// One call = one round trip to GitHub. The store decides what's new.
    /// </summary>
    IAsyncEnumerable<GhNotification> PollAsync(CancellationToken cancellationToken);
}

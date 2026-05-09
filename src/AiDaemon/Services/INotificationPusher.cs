using AiDaemon.Models;

namespace AiDaemon.Services;

public interface INotificationPusher
{
    /// <summary>
    /// First push for a freshly-spawned RC session. The user taps it on their phone to open
    /// the live session.
    /// </summary>
    Task PushSessionLinkAsync(
        string url,
        BranchInfo branch,
        GhNotification notification,
        TriageVerdict verdict,
        CancellationToken cancellationToken);

    /// <summary>
    /// Followup for an already-active RC session. Same URL, lower priority, "heads-up" prefix.
    /// </summary>
    Task PushHeadsUpAsync(
        string url,
        BranchInfo branch,
        GhNotification notification,
        TriageVerdict verdict,
        CancellationToken cancellationToken);
}

/// <summary>
/// Phase 4 placeholder. Logs what it would push at <c>Information</c> level. Phase 5 replaces
/// this with <c>NtfyPusher</c>.
/// </summary>
public class NoopNotificationPusher : INotificationPusher
{
    readonly Microsoft.Extensions.Logging.ILogger<NoopNotificationPusher> _logger;

    public NoopNotificationPusher(Microsoft.Extensions.Logging.ILogger<NoopNotificationPusher> logger)
    {
        _logger = logger;
    }

    public Task PushSessionLinkAsync(
        string url, BranchInfo branch, GhNotification notification, TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[push] session-link branch={Branch} url={Url} title={Title} summary={Summary}",
            branch.Key, url, notification.Subject.Title, verdict.Summary);
        return Task.CompletedTask;
    }

    public Task PushHeadsUpAsync(
        string url, BranchInfo branch, GhNotification notification, TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[push] heads-up branch={Branch} url={Url} title={Title} summary={Summary}",
            branch.Key, url, notification.Subject.Title, verdict.Summary);
        return Task.CompletedTask;
    }
}

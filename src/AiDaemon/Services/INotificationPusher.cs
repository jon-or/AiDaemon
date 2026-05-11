using AiDaemon.Models;

namespace AiDaemon.Services;

public interface INotificationPusher
{
    /// <summary>
    /// First push for a freshly-spawned RC session. Pass the bridge URL for a normal spawn,
    /// or the literal string <c>"Not Available"</c> when the RC relay is down — the push
    /// still goes out so the user sees the actionable event, and the pusher omits the click
    /// target instead of producing an untappable button.
    /// </summary>
    /// <param name="subjectTitle">The GitHub issue/PR title — used as the ntfy push title.
    /// Falls back to the branch name when empty.</param>
    Task PushSessionLinkAsync(
        string url,
        BranchInfo branch,
        string subjectTitle,
        TriageVerdict verdict,
        CancellationToken cancellationToken);

    /// <summary>
    /// Followup for an already-active RC session. Same URL, lower priority, no prefix change
    /// (priority alone distinguishes the two on the phone).
    /// </summary>
    Task PushHeadsUpAsync(
        string url,
        BranchInfo branch,
        string subjectTitle,
        TriageVerdict verdict,
        CancellationToken cancellationToken);

    /// <summary>
    /// Operator-facing alert (no branch context). High priority. Used when the daemon
    /// itself is in trouble — auth failures at startup, gh CLI missing, etc. — so the
    /// operator sees it on their phone even though it isn't a real notification event.
    /// </summary>
    Task PushAlertAsync(
        string title,
        string body,
        CancellationToken cancellationToken);
}

/// <summary>
/// Test / debug pusher that logs every call at <c>Information</c> instead of going to the
/// network. Useful when iterating on the pipeline without buzzing the phone, and as the
/// drop-in for unit tests that want a non-Mock fake.
/// </summary>
public class NoopNotificationPusher : INotificationPusher
{
    readonly Microsoft.Extensions.Logging.ILogger<NoopNotificationPusher> _logger;

    public NoopNotificationPusher(Microsoft.Extensions.Logging.ILogger<NoopNotificationPusher> logger)
    {
        _logger = logger;
    }

    public Task PushSessionLinkAsync(
        string url, BranchInfo branch, string subjectTitle, TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[push] session-link branch={Branch} url={Url} title={Title} summary={Summary}",
            branch.Key, url, subjectTitle, verdict.Summary);
        return Task.CompletedTask;
    }

    public Task PushHeadsUpAsync(
        string url, BranchInfo branch, string subjectTitle, TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[push] heads-up branch={Branch} url={Url} title={Title} summary={Summary}",
            branch.Key, url, subjectTitle, verdict.Summary);
        return Task.CompletedTask;
    }

    public Task PushAlertAsync(string title, string body, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[push] alert title={Title} body={Body}", title, body);
        return Task.CompletedTask;
    }
}

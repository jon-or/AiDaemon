using AiDaemon.Models;

namespace AiDaemon.Services;

public interface IGhClient
{
    /// <summary>
    /// Invokes <c>gh api &lt;path&gt;</c> and deserializes stdout to <typeparamref name="T"/>.
    /// Throws <see cref="GhAuthException"/> on 401/403, <see cref="GhCliException"/> on other non-zero exits.
    /// </summary>
    Task<T> ApiAsync<T>(string path, CancellationToken cancellationToken);

    /// <summary>
    /// <c>gh api -X &lt;method&gt; &lt;path&gt;</c> with no body / no return parsing. Used for PATCH/POST writes.
    /// </summary>
    Task ApiVoidAsync(string method, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Lists participating notifications updated after <paramref name="since"/> (or all recent if null).
    /// Backed by <c>/notifications?participating=true&amp;all=true&amp;since=&lt;iso&gt;</c>, paginated.
    /// </summary>
    Task<IReadOnlyList<GhNotification>> ListNotificationsAsync(DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a notification thread as read via PATCH <c>/notifications/threads/{id}</c>.
    /// </summary>
    Task MarkThreadReadAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>
    /// GET <c>/notifications/threads/{id}</c> — returns the single notification fresh from
    /// GitHub. Used by the tray Retry path: the cursor has long since advanced past this
    /// thread, so the regular <see cref="ListNotificationsAsync"/> stream won't yield it
    /// again on the next poll, even after we delete the dedup row. Returns <c>null</c> when
    /// GitHub responds 404 (thread expired or never existed).
    /// </summary>
    Task<GhNotification?> GetNotificationThreadAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>
    /// Dereferences <c>subject.latest_comment_url</c> (or the subject URL itself if there's no latest comment).
    /// Returns <c>null</c> if the URL is missing.
    /// </summary>
    Task<CommentInfo?> GetCommentAsync(string url, CancellationToken cancellationToken);

    Task<PrInfo> GetPullRequestAsync(string repoFullName, int prNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the PR number of the single open PR whose head ref equals
    /// <paramref name="branch"/>, or <c>null</c> if there are zero or more than one.
    /// Used by <see cref="IBranchResolver"/> to cross-link an issue notification to its
    /// associated PR (the common case is one issue → one PR named after the issue).
    /// </summary>
    Task<int?> FindOpenPrNumberForBranchAsync(string repoFullName, string branch, CancellationToken cancellationToken);

    /// <summary>Lightweight auth probe: <c>gh api /user</c>. Throws on failure.</summary>
    Task<string> WhoAmIAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>gh auth status</c> and returns the human-readable report (host, account,
    /// scopes). Throws <see cref="GhAuthException"/> when gh exits non-zero — the command's
    /// whole purpose is reporting auth state, so a non-zero exit is by definition an auth
    /// problem. Used by the startup health probe to surface "you forgot to run gh auth
    /// login" loudly enough that an operator sees it before the first poll fails.
    /// </summary>
    Task<string> AuthStatusAsync(CancellationToken cancellationToken);
}

public class GhCliException : Exception
{
    public int ExitCode { get; }
    public string Stderr { get; }

    public GhCliException(int exitCode, string stderr, string message)
        : base(message)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }
}

public class GhAuthException : GhCliException
{
    public GhAuthException(int exitCode, string stderr)
        : base(exitCode, stderr, $"gh auth failure (exit {exitCode}): {stderr.Trim()}")
    {
    }
}

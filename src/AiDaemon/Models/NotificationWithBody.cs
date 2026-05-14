namespace AiDaemon.Models;

/// <summary>
/// A polled GitHub notification paired with the comment body and author login fetched at
/// L1 author-check time. Both are carried alongside so downstream segments (agent triage,
/// pre-run, push) don't re-fetch the same URL.
/// </summary>
/// <param name="Notification">The raw notification from /notifications.</param>
/// <param name="CommentBody">Body of <c>subject.latest_comment_url</c> (or empty when the
/// notification has no comment URL — e.g. <c>review_requested</c>).</param>
/// <param name="CommentAuthor">GitHub <c>user.login</c> of the comment author (or empty
/// when no comment was fetched). Used by the pre-run agent to credit the requester in its
/// summary message.</param>
public record NotificationWithBody(
    GhNotification Notification,
    string CommentBody,
    string CommentAuthor = "")
{
    /// <summary>
    /// Body of the issue/PR conversation comment that came immediately before
    /// <see cref="CommentBody"/>, or empty when none was fetched (no prior comment, fetch
    /// failed, or the notification didn't surface an issue/PR number). Triage and the
    /// pre-run agent get this so they can resolve a latest comment that references the one
    /// before it ("see above", "as I said earlier", short follow-ups, etc.) without needing
    /// to spend a tool call to fetch it themselves.
    /// </summary>
    public string PriorCommentBody { get; init; } = "";

    /// <summary>GitHub login of whoever posted <see cref="PriorCommentBody"/>, or empty when none.</summary>
    public string PriorCommentAuthor { get; init; } = "";
}

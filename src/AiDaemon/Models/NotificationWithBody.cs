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
    string CommentAuthor = "");

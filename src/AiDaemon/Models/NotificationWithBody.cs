namespace AiDaemon.Models;

/// <summary>
/// A polled GitHub notification paired with the comment body fetched at L1 author-check time.
/// The body is carried alongside so downstream segments (agent triage, pre-run, push) don't
/// re-fetch the same URL.
/// </summary>
public record NotificationWithBody(GhNotification Notification, string CommentBody);

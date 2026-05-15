using System.Text.Json.Serialization;

namespace AiDaemon.Models;

/// <summary>
/// Minimal projection of a PR review payload from
/// GET /repos/{owner}/{repo}/pulls/{n}/reviews. Used to recover a "what fired this
/// notification" body when <c>subject.latest_comment_url</c> is null on a PR notification —
/// the PR-level review events (APPROVED, CHANGES_REQUESTED, COMMENTED with a top-level
/// body) don't surface as comments in the notification payload, so triage has to fetch
/// the reviews list and match by submission time.
/// </summary>
public class ReviewInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED, PENDING.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("user")]
    public GhUserRef User { get; set; } = new();

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset? SubmittedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

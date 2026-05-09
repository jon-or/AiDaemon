using System.Text.Json.Serialization;

namespace AiDaemon.Models;

/// <summary>
/// Subset of GitHub's /notifications response we care about.
/// Field names match the API JSON via JsonPropertyName so we can deserialize directly from gh stdout.
/// </summary>
public class GhNotification
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("unread")]
    public bool Unread { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("repository")]
    public GhRepositoryRef Repository { get; set; } = new();

    [JsonPropertyName("subject")]
    public GhNotificationSubject Subject { get; set; } = new();
}

public class GhRepositoryRef
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";
}

public class GhNotificationSubject
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>e.g. https://api.github.com/repos/owner/repo/issues/123 — last segment is the number.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("latest_comment_url")]
    public string? LatestCommentUrl { get; set; }

    /// <summary>"Issue", "PullRequest", "Discussion", "Release", "Commit", "CheckSuite".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

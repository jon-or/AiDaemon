using System.Text.Json.Serialization;

namespace AiDaemon.Models;

/// <summary>
/// Minimal projection of an issue/PR comment payload returned from
/// GET subject.latest_comment_url. The API also returns this shape for the
/// issue body itself when latest_comment_url points at the issue endpoint.
/// </summary>
public class CommentInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("user")]
    public GhUserRef User { get; set; } = new();

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

public class GhUserRef
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

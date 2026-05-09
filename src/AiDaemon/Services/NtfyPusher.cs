using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

/// <summary>
/// Posts to ntfy via the JSON publish endpoint
/// (https://docs.ntfy.sh/publish/#publish-as-json). JSON form is preferred over the
/// header-form because GitHub titles, verdict summaries, and L3 reasoning routinely
/// carry non-ASCII (emoji, smart quotes, accented names) — those would need RFC 2047
/// encoding in HTTP headers and silently mojibake otherwise.
/// </summary>
/// <remarks>
/// Push is best-effort. Network errors and non-2xx responses are logged at Warning and
/// swallowed; the dispatcher's branch state is already persisted by the time we get here,
/// and a missed push on a successfully-spawned RC session is recoverable (the user can find
/// the URL in the log or the SQLite branches table).
/// </remarks>
public class NtfyPusher : INotificationPusher
{
    readonly HttpClient _http;
    readonly DaemonOptions _options;
    readonly ILogger<NtfyPusher> _logger;

    public NtfyPusher(HttpClient http, IOptions<DaemonOptions> options, ILogger<NtfyPusher> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public Task PushSessionLinkAsync(
        string url, BranchInfo branch, GhNotification notification, TriageVerdict verdict,
        CancellationToken cancellationToken)
        => PostAsync(
            title: BuildTitle(branch, verdict, prefix: ""),
            message: BuildBody(notification, verdict, url),
            priority: _options.Ntfy.PriorityHigh,
            clickUrl: url,
            actionLabel: "Open session",
            cancellationToken: cancellationToken);

    public Task PushHeadsUpAsync(
        string url, BranchInfo branch, GhNotification notification, TriageVerdict verdict,
        CancellationToken cancellationToken)
        => PostAsync(
            title: BuildTitle(branch, verdict, prefix: "[update] "),
            message: BuildBody(notification, verdict, url),
            priority: _options.Ntfy.PriorityNormal,
            clickUrl: url,
            actionLabel: "Resume session",
            cancellationToken: cancellationToken);

    static string BuildTitle(BranchInfo branch, TriageVerdict verdict, string prefix)
    {
        var summary = string.IsNullOrWhiteSpace(verdict.Summary) ? "" : $" {verdict.Summary}";
        // ntfy renders titles in roughly one line on iOS; cap so the suffix doesn't get lost
        // behind a long branch slug + summary.
        return $"{prefix}[{branch.Repo}:{branch.Branch}]{summary}".TruncateWithEllipsis(120);
    }

    static string BuildBody(GhNotification notification, TriageVerdict verdict, string url)
    {
        // Lead with the GitHub subject so the push shows what the conversation is about even
        // when Why is just a short tag (e.g. "review_requested" with no L3 reasoning).
        var subject = notification.Subject.Title;
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(subject))
            sb.Append(subject);

        // When the URL isn't a real https link (the dispatcher passes "Not Available" when
        // the RC relay is down), surface that in the body — the click/action button is
        // suppressed in PostAsync so the body is the only place the user sees the status.
        if (!IsRealUrl(url))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("Session: ").Append(string.IsNullOrWhiteSpace(url) ? "Not Available" : url);
        }

        if (!string.IsNullOrWhiteSpace(verdict.Why))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(verdict.Why);
        }

        return sb.ToString();
    }

    async Task PostAsync(
        string title, string message, int priority, string clickUrl, string actionLabel,
        CancellationToken cancellationToken)
    {
        var topic = _options.Ntfy.Topic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            // Missing topic isn't fatal — dispatch already logged the spawned URL at Information.
            // Treat this as a config-fix-it warning, not a crash.
            _logger.LogWarning("Ntfy.Topic not configured — skipping push (title={Title})", title);
            return;
        }

        var payload = new NtfyPayload
        {
            Topic = topic,
            Title = title,
            Message = message,
            Priority = priority,
            Tags = new[] { "robot" },
        };

        // Only set click/actions when we have a real URL — passing "Not Available" or empty
        // through to ntfy would render an action button that opens nowhere.
        if (IsRealUrl(clickUrl))
        {
            payload.Click = clickUrl;
            payload.Actions = new[]
            {
                new NtfyAction { Action = "view", Label = actionLabel, Url = clickUrl },
            };
        }

        var server = string.IsNullOrEmpty(_options.Ntfy.Server) ? "https://ntfy.sh" : _options.Ntfy.Server;
        var requestUri = server.TrimEnd('/') + "/";

        try
        {
            using var resp = await _http.PostAsJsonAsync(requestUri, payload, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "ntfy push returned {Status} {Reason}: {Body}",
                    (int)resp.StatusCode, resp.ReasonPhrase, body.TruncateWithEllipsis(200));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ntfy push failed: {Title}", title);
        }
    }

    static bool IsRealUrl(string s)
        => Uri.TryCreate(s, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // ntfy JSON publish payload — field names per https://docs.ntfy.sh/publish/#publish-as-json.
    class NtfyPayload
    {
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = Array.Empty<string>();

        [JsonPropertyName("click")]
        public string? Click { get; set; }

        [JsonPropertyName("actions")]
        public NtfyAction[]? Actions { get; set; }
    }

    class NtfyAction
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }
}

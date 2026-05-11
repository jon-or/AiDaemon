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
    /// <summary>
    /// Fallback target for the "Open Claude" action button when the RC relay is down. Drops
    /// the user at the claude.ai code home where they can find their session manually
    /// (better than a broken link, broken enough that they immediately know RC was down).
    /// </summary>
    internal const string ClaudeFallbackUrl = "https://claude.ai/code";

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
        string url, BranchInfo branch, string subjectTitle, TriageVerdict verdict,
        CancellationToken cancellationToken)
        => PostAsync(
            title: BuildTitle(branch, subjectTitle),
            message: BuildBody(branch, verdict, url),
            priority: _options.Ntfy.PriorityHigh,
            branch: branch,
            sessionUrl: url,
            cancellationToken: cancellationToken);

    public Task PushHeadsUpAsync(
        string url, BranchInfo branch, string subjectTitle, TriageVerdict verdict,
        CancellationToken cancellationToken)
        => PostAsync(
            title: BuildTitle(branch, subjectTitle),
            message: BuildBody(branch, verdict, url),
            priority: _options.Ntfy.PriorityNormal,
            branch: branch,
            sessionUrl: url,
            cancellationToken: cancellationToken);

    public Task PushAlertAsync(string title, string body, CancellationToken cancellationToken)
        => PostAsync(
            title: title,
            message: body,
            priority: _options.Ntfy.PriorityHigh,
            branch: null,
            sessionUrl: "",
            tags: new[] { "warning" },
            cancellationToken: cancellationToken);

    /// <summary>
    /// Title is the GitHub issue/PR title — what the conversation is actually about. Falls
    /// back to the branch name when no subject title was supplied (defensive; in practice
    /// the dispatcher always has one). PriorityHigh vs PriorityNormal distinguishes a
    /// fresh spawn from a heads-up.
    /// </summary>
    static string BuildTitle(BranchInfo branch, string subjectTitle)
    {
        var title = string.IsNullOrWhiteSpace(subjectTitle) ? branch.Branch : subjectTitle;
        return title.TruncateWithEllipsis(120);
    }

    /// <summary>
    /// Markdown body. Branch (qualified <c>repo:branch</c>) goes first as an inline-code
    /// span — visually subordinate to the title and the summary, but still readable. The
    /// summary itself may contain markdown the pre-run agent emitted (bold for the
    /// requester's name, code spans for filenames, etc.). The "Session Not Available" line
    /// only appears when the RC relay is down — when a real URL is in hand, the Open
    /// Claude button covers it.
    /// </summary>
    static string BuildBody(BranchInfo branch, TriageVerdict verdict, string url)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('`').Append(branch.Repo).Append(':').Append(branch.Branch).Append('`');

        if (!string.IsNullOrWhiteSpace(verdict.Summary))
            sb.Append("\n\n").Append(verdict.Summary);

        if (!IsRealUrl(url))
            sb.Append("\n\n_Session Not Available_");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the action-button strip: Open PR (when the branch resolved to a PR), Open
    /// Issue (when it resolved to an issue), Open Claude (always, RC URL or fallback).
    /// ntfy supports up to 3 custom view actions per notification — exactly enough.
    /// Returns an empty array when <paramref name="branch"/> is null (operator alerts have
    /// no branch context, so no buttons fit).
    /// </summary>
    static NtfyAction[] BuildActions(BranchInfo? branch, string sessionUrl)
    {
        if (branch == null)
            return Array.Empty<NtfyAction>();

        var list = new List<NtfyAction>(3);

        if (branch.PrNumber is int pr)
            list.Add(new NtfyAction
            {
                Action = "view",
                Label = "Open PR",
                Url = $"https://github.com/{branch.Repo}/pull/{pr}",
            });

        if (branch.IssueNumber is int issue)
            list.Add(new NtfyAction
            {
                Action = "view",
                Label = "Open Issue",
                Url = $"https://github.com/{branch.Repo}/issues/{issue}",
            });

        list.Add(new NtfyAction
        {
            Action = "view",
            Label = "Open Claude",
            Url = IsRealUrl(sessionUrl) ? sessionUrl : ClaudeFallbackUrl,
        });

        return list.ToArray();
    }

    async Task PostAsync(
        string title, string message, int priority, BranchInfo? branch, string sessionUrl,
        CancellationToken cancellationToken,
        string[]? tags = null)
    {
        var topic = _options.Ntfy.Topic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            // Missing topic isn't fatal — dispatch already logged the spawned URL at Information.
            // Treat this as a config-fix-it warning, not a crash.
            _logger.LogWarning("Ntfy.Topic not configured — skipping push (title={Title})", title);
            return;
        }

        // Deliberately omit `click` so the ntfy app doesn't auto-render the COPY LINK /
        // OPEN LINK buttons it generates from a click target — the three custom actions
        // (Open PR / Open Issue / Open Claude) are the only buttons we want.
        var payload = new NtfyPayload
        {
            Topic = topic,
            Title = title,
            Message = message,
            Markdown = true,
            Priority = priority,
            Tags = tags ?? new[] { "robot" },
            Actions = BuildActions(branch, sessionUrl),
        };

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

        // ntfy renders the message as markdown when this is true (CommonMark + GFM,
        // per https://docs.ntfy.sh/publish/#markdown-formatting).
        [JsonPropertyName("markdown")]
        public bool Markdown { get; set; }

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

using System.Text.Json;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class GhClient : IGhClient
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly IProcessRunner _runner;
    readonly DaemonOptions _options;
    readonly ILogger<GhClient> _logger;

    public GhClient(IProcessRunner runner, IOptions<DaemonOptions> options, ILogger<GhClient> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T> ApiAsync<T>(string path, CancellationToken cancellationToken)
    {
        var result = await RunGhAsync(new[] { "api", path }, cancellationToken);
        return Deserialize<T>(result.Stdout, path);
    }

    public Task ApiVoidAsync(string method, string path, CancellationToken cancellationToken)
        => RunGhAsync(new[] { "api", "-X", method, path }, cancellationToken);

    public async Task<IReadOnlyList<GhNotification>> ListNotificationsAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        // all=true: notifications come in pre-marked-read for some accounts (the unread filter
        // misses too much), so we drive idempotency from a date cursor + the processed table.
        // --paginate concatenates pages by walking Link headers — fine because `since` keeps the
        // window bounded.
        var query = "/notifications?participating=true&all=true&per_page=50";
        if (since.HasValue)
            query += $"&since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";

        var result = await RunGhAsync(new[] { "api", "--paginate", query }, cancellationToken);

        var list = Deserialize<List<GhNotification>>(result.Stdout, "/notifications");
        return list;
    }

    public Task MarkThreadReadAsync(string threadId, CancellationToken cancellationToken)
        => ApiVoidAsync("PATCH", $"/notifications/threads/{threadId}", cancellationToken);

    public async Task<CommentInfo?> GetCommentAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var path = ToGhApiPath(url);
        try
        {
            var result = await RunGhAsync(new[] { "api", path }, cancellationToken);
            return Deserialize<CommentInfo>(result.Stdout, path);
        }
        catch (GhCliException ex) when (ex.Stderr.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            // Comment was deleted between notification fetch and dereference.
            _logger.LogDebug("Comment URL 404'd: {Url}", url);
            return null;
        }
    }

    public Task<PrInfo> GetPullRequestAsync(string repoFullName, int prNumber, CancellationToken cancellationToken)
        => ApiAsync<PrInfo>($"/repos/{repoFullName}/pulls/{prNumber}", cancellationToken);

    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken)
    {
        var doc = await ApiAsync<JsonElement>("/user", cancellationToken);
        return doc.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
    }

    async Task<ProcessResult> RunGhAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_options.GhPath, args, cancellationToken: cancellationToken);

        if (result.Succeeded)
            return result;

        if (LooksLikeAuthFailure(result.Stderr))
        {
            _logger.LogError(
                "gh auth failure (exit {ExitCode}). Run `gh auth login` to authenticate. Stderr: {Stderr}",
                result.ExitCode, result.Stderr.Trim());
            throw new GhAuthException(result.ExitCode, result.Stderr);
        }

        var msg = $"gh {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr.Trim()}";
        throw new GhCliException(result.ExitCode, result.Stderr, msg);
    }

    static bool LooksLikeAuthFailure(string stderr)
        => stderr.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("GH_TOKEN", StringComparison.Ordinal)
        || (stderr.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("required", StringComparison.OrdinalIgnoreCase));

    static T Deserialize<T>(string stdout, string context)
    {
        try
        {
            var v = JsonSerializer.Deserialize<T>(stdout, JsonOpts)
                ?? throw new InvalidOperationException($"gh returned null JSON for {context}");
            return v;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse gh stdout for {context}: {ex.Message}. First 200 chars: {Truncate(stdout, 200)}",
                ex);
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// `gh api` accepts either an absolute https://api.github.com URL or just the path part.
    /// Notification subject URLs come back absolute; strip the host so the call matches the
    /// notification helper's path-only style and works with hosts other than github.com.
    /// </summary>
    static string ToGhApiPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.PathAndQuery;

        return url;
    }
}

using System.Text.Json;
using AiDaemon.Common;
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

    public async Task<GhNotification?> GetNotificationThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return null;

        var path = $"/notifications/threads/{threadId}";
        try
        {
            var result = await RunGhAsync(new[] { "api", path }, cancellationToken);
            return Deserialize<GhNotification>(result.Stdout, path);
        }
        catch (GhCliException ex) when (ex.Stderr.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            // Thread aged out of GitHub's notification window (typically ~5 months) or never
            // existed for this account. Caller surfaces this to the operator rather than
            // exploding the tray.
            _logger.LogDebug("notifications/threads/{ThreadId} 404'd", threadId);
            return null;
        }
    }

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

    public async Task<int?> FindOpenPrNumberForBranchAsync(
        string repoFullName, string branch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoFullName) || string.IsNullOrWhiteSpace(branch))
            return null;

        var slash = repoFullName.IndexOf('/');
        if (slash <= 0 || slash == repoFullName.Length - 1)
            return null;
        var owner = repoFullName[..slash];

        // GitHub's pulls list endpoint accepts head=<owner>:<branch>. Fork PRs would be
        // owner=<forker>; we only care about same-repo PRs since that's the worktree case
        // the daemon supports.
        var path = $"/repos/{repoFullName}/pulls?state=open&head={Uri.EscapeDataString($"{owner}:{branch}")}";

        List<PrInfo> prs;
        try
        {
            prs = await ApiAsync<List<PrInfo>>(path, cancellationToken);
        }
        catch (GhCliException ex) when (ex.Stderr.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "The common case" — exactly one open PR for the branch. Zero (issue-only branch)
        // or more than one (rare; conflicting reopens) → return null and let the caller
        // omit the Open PR button rather than guess.
        return prs.Count == 1 ? prs[0].Number : null;
    }

    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken)
    {
        var doc = await ApiAsync<JsonElement>("/user", cancellationToken);
        return doc.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
    }

    public async Task<string> AuthStatusAsync(CancellationToken cancellationToken)
    {
        // Bypass RunGhAsync's auth-detection path: non-zero exit from `gh auth status` is by
        // definition an auth failure (the command's whole job is to report auth state), so
        // any failure here should always raise GhAuthException regardless of stderr keywords.
        var result = await _runner.RunAsync(
            _options.GhPath, new[] { "auth", "status" }, cancellationToken: cancellationToken);

        // gh 2.x writes the human-readable report to stderr; newer versions may use stdout.
        // Coalesce so the caller's log line carries the actual report text regardless.
        var output = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;

        if (!result.Succeeded)
            throw new GhAuthException(result.ExitCode, output);

        return output.Trim();
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
                $"Failed to parse gh stdout for {context}: {ex.Message}. First 200 chars: {stdout.TruncateWithEllipsis(200)}",
                ex);
        }
    }

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

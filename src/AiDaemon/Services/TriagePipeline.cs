using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class TriagePipeline : ITriagePipeline
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The set of subject types that can ever be actionable. Everything else drops at L1.</summary>
    static readonly HashSet<string> SupportedSubjectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Issue",
        "PullRequest",
    };

    /// <summary>
    /// Per-call timeout for the L3 agent run. Bigger than Phase 2's classifier window — the
    /// agent now reads files, runs git, edits code; multi-turn tool use takes time.
    /// </summary>
    static readonly TimeSpan L3Timeout = TimeSpan.FromMinutes(5);

    readonly IGhClient _gh;
    readonly IClaudeRunner _claude;
    readonly IStateStore _store;
    readonly DaemonOptions _options;
    readonly ILogger<TriagePipeline> _logger;

    static readonly System.Reflection.Assembly OwnAssembly = typeof(TriagePipeline).Assembly;
    readonly Lazy<string> _systemPrompt = new(() => EmbeddedResource.Load(OwnAssembly, "system-prompt.md"));
    readonly Lazy<string> _schema = new(() => EmbeddedResource.Load(OwnAssembly, "schema.json"));
    readonly Lazy<List<Regex>> _l2Patterns;
    readonly HashSet<string> _actionableReasons;
    readonly HashSet<string> _botBlocklist;

    public TriagePipeline(
        IGhClient gh,
        IClaudeRunner claude,
        IStateStore store,
        IOptions<DaemonOptions> options,
        ILogger<TriagePipeline> logger)
    {
        _gh = gh;
        _claude = claude;
        _store = store;
        _options = options.Value;
        _logger = logger;

        _l2Patterns = new Lazy<List<Regex>>(() =>
            _options.Triage.L2DropPatterns
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
                .ToList());

        _actionableReasons = new HashSet<string>(_options.ActionableReasons, StringComparer.OrdinalIgnoreCase);
        _botBlocklist = new HashSet<string>(_options.BotAuthorBlocklist, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(TriageVerdict? Verdict, string CommentBody, string CommentAuthor)> QuickTriageAsync(GhNotification n, CancellationToken cancellationToken)
    {
        // ---------- L1: type / reason / rate / author ----------
        if (!SupportedSubjectTypes.Contains(n.Subject.Type))
            return (TriageVerdict.Drop($"unsupported subject type: {n.Subject.Type}"), "", "");

        if (!_actionableReasons.Contains(n.Reason))
            return (TriageVerdict.Drop($"reason '{n.Reason}' not in ActionableReasons"), "", "");

        // Rate-limit is checked read-only here — the count records dispatched actions, not
        // notifications considered, so the increment lives at the dispatch decision in the
        // worker. Reading first means rate-limited threads don't even pay for the comment fetch.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentCount = await _store.GetRateLimitAsync(n.Id, today, cancellationToken);
        if (currentCount >= _options.Triage.MaxActionsPerThreadPerDay)
        {
            _logger.LogInformation(
                "rate-limit drop thread={ThreadId} count={Count} max={Max}",
                n.Id, currentCount, _options.Triage.MaxActionsPerThreadPerDay);
            return (TriageVerdict.Drop(
                $"thread daily rate limit ({currentCount}/{_options.Triage.MaxActionsPerThreadPerDay})"), "", "");
        }

        // L1 author check requires the comment body. Fetch once; the body + author are
        // also returned so callers can pass them through to the agent without re-fetching
        // (and so the pre-run can credit the requester by name in its summary).
        CommentInfo? comment = null;
        if (!string.IsNullOrWhiteSpace(n.Subject.LatestCommentUrl))
            comment = await _gh.GetCommentAsync(n.Subject.LatestCommentUrl!, cancellationToken);

        var author = comment?.User.Login ?? "";
        var body = comment?.Body ?? "";

        if (!string.IsNullOrEmpty(_options.AiUserLogin) &&
            string.Equals(author, _options.AiUserLogin, StringComparison.OrdinalIgnoreCase))
            return (TriageVerdict.Drop($"self-authored by {author}"), body, author);

        if (!string.IsNullOrEmpty(author) && _botBlocklist.Contains(author))
            return (TriageVerdict.Drop($"blocklisted bot author: {author}"), body, author);

        // ---------- L2: regex content filter ----------
        var stripped = StripQuotedReplies(body);
        foreach (var pat in _l2Patterns.Value)
        {
            if (pat.IsMatch(stripped))
                return (TriageVerdict.Drop($"L2 regex match: /{pat}/"), body, author);
        }

        // Defer to agent triage.
        return (null, body, author);
    }

    public async Task<IReadOnlyList<NotificationWithBody>> EnrichWithPriorCommentsAsync(
        IReadOnlyList<NotificationWithBody> items, BranchInfo branch, CancellationToken cancellationToken)
    {
        // Branch number drives the listing. PRs are issues for the comments endpoint, so PrNumber
        // is preferred (it points at the active conversation); IssueNumber is the fallback for
        // issue-only branches. With neither, we skip — there's no way to list anything.
        var number = branch.PrNumber ?? branch.IssueNumber;
        if (number is null || number <= 0)
            return items;

        // One listing per branch, regardless of how many notifications coalesced onto it. Two
        // newest comments are enough: each item drops its own latest from the dedupe and keeps
        // whatever remains as the prior. If both items in a batch happen to share the same
        // latest_comment_url, they share the same prior — that's fine.
        IReadOnlyList<CommentInfo> recent;
        try
        {
            recent = await _gh.ListRecentIssueCommentsAsync(branch.Repo, number.Value, perPage: 2, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "prior-comment fetch failed branch={Branch} number={Number} — proceeding without",
                branch.Key, number);
            return items;
        }

        if (recent.Count == 0)
            return items;

        var enriched = new List<NotificationWithBody>(items.Count);
        foreach (var item in items)
        {
            // The latest_comment_url's id identifies which comment in the listing is "the
            // latest" so we can pick the one *before* it. If the URL isn't parseable (PR review
            // comment / review URL — different endpoints), we still have the most-recent issue
            // comment in `recent[0]` as useful context; take it as the prior since we know it
            // isn't the one that fired the notification.
            var latestId = ParseCommentIdFromUrl(item.Notification.Subject.LatestCommentUrl);
            var prior = recent.FirstOrDefault(c => latestId is null || c.Id != latestId.Value);
            if (prior is null)
            {
                enriched.Add(item);
                continue;
            }

            enriched.Add(item with
            {
                PriorCommentBody = prior.Body ?? "",
                PriorCommentAuthor = prior.User?.Login ?? "",
            });
        }

        return enriched;
    }

    /// <summary>
    /// Pulls the trailing numeric segment out of a GitHub comment URL
    /// (<c>https://api.github.com/repos/o/r/issues/comments/12345</c>). Returns <c>null</c>
    /// for URLs that don't end in a number (subject URLs, malformed input, etc.).
    /// </summary>
    internal static long? ParseCommentIdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var slash = url.LastIndexOf('/');
        if (slash < 0 || slash == url.Length - 1) return null;
        var tail = url.AsSpan(slash + 1);
        // Trim any query string (?foo=bar) — gh URLs don't carry one today, but defensive.
        var q = tail.IndexOf('?');
        if (q >= 0) tail = tail[..q];
        return long.TryParse(tail, out var id) ? id : null;
    }

    public async Task<TriageVerdict> AgentTriageAsync(IReadOnlyList<NotificationWithBody> items, BranchInfo branch, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            throw new ArgumentException("AgentTriage requires at least one notification.", nameof(items));

        // Triage runs in a stable scratch cwd (NOT the worktree) so claude doesn't pay the
        // CLAUDE.md auto-discovery + plugin sync cost on every notification. Reusing the same
        // scratch dir across calls also keeps the prompt cache warm.
        var scratchDir = Path.Combine(_options.DataDir, "triage-scratch");

        var userInput = BuildAgentInput(items, branch);

        ClaudeJsonResult result;
        try
        {
            result = await _claude.RunHeadlessJsonAsync(
                systemPrompt: _systemPrompt.Value,
                userInput: userInput,
                schemaJson: _schema.Value,
                model: _options.Triage.Model,
                workingDirectory: scratchDir,
                timeout: L3Timeout,
                cancellationToken: cancellationToken,
                sessionId: null,         // throwaway — no need to persist a classifier turn
                permissionMode: null);   // classifier doesn't use tools
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "agent triage failed branch={Branch} count={Count} — defaulting to actionable",
                branch.Key, items.Count);
            return DefaultActionable($"agent error ({ex.GetType().Name}) — actionable by default");
        }

        if (result.IsError)
        {
            _logger.LogWarning(
                "agent triage is_error=true branch={Branch} count={Count} result={Result} — defaulting to actionable",
                branch.Key, items.Count, result.Result);
            return DefaultActionable("agent reported is_error=true — actionable by default");
        }

        TriageStructuredOutput? llm = null;
        if (result.StructuredOutput is JsonElement so)
        {
            try
            {
                llm = JsonSerializer.Deserialize<TriageStructuredOutput>(so.GetRawText(), JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "agent triage produced unparseable structured_output branch={Branch}", branch.Key);
            }
        }

        if (llm == null)
        {
            _logger.LogWarning(
                "agent triage returned no structured output branch={Branch} — defaulting to actionable", branch.Key);
            return DefaultActionable("agent returned no structured output — actionable by default");
        }

        // Trust the agent's verdict directly — no post-hoc bias rule.
        var action = string.Equals(llm.Action, "drop", StringComparison.OrdinalIgnoreCase)
            ? TriageAction.Drop
            : TriageAction.Actionable;

        // Triage no longer produces a `summary` — that's the pre-run agent's job. Push body
        // shows the raw branch line + (when pre-run runs) the pre-run's summary; heads-up /
        // cross-tick paths skip pre-run and the body is just the branch line.
        var verdict = new TriageVerdict(action, llm.Why, Summary: "", llm.Confidence);

        _logger.LogInformation(
            "L3 verdict branch={Branch} count={Count} action={Action} confidence={Confidence:F2} why={Why}",
            branch.Key, items.Count, verdict.Action, verdict.Confidence, verdict.Why);

        return verdict;
    }

    /// <summary>
    /// The user message the agent receives. When multiple notifications resolved to the same
    /// branch (e.g. an issue mention + a PR review on the PR closing it), all of them are
    /// included so the classifier can weigh them together.
    /// </summary>
    static string BuildAgentInput(IReadOnlyList<NotificationWithBody> items, BranchInfo branch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Branch: {branch.Branch}");
        sb.AppendLine($"- Repository: {branch.Repo}");
        if (branch.PrNumber is int pr) sb.AppendLine($"- PR: #{pr}");
        if (branch.IssueNumber is int issue) sb.AppendLine($"- Issue: #{issue}");
        sb.AppendLine();

        if (items.Count == 1)
            sb.AppendLine("## Notification");
        else
            sb.AppendLine($"## {items.Count} notifications on this branch in the current poll");

        // Order by updated_at so the agent reads them in the order they fired.
        var ordered = items.OrderBy(i => i.Notification.UpdatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var n = ordered[i].Notification;
            var body = ordered[i].CommentBody;
            var priorBody = ordered[i].PriorCommentBody;
            var priorAuthor = ordered[i].PriorCommentAuthor;

            sb.AppendLine();
            sb.AppendLine($"### {i + 1}. {n.Subject.Type} — reason `{n.Reason}` — {n.UpdatedAt:O}");
            sb.AppendLine($"- Title: {n.Subject.Title}");
            if (!string.IsNullOrEmpty(n.Subject.LatestCommentUrl))
                sb.AppendLine($"- Latest comment URL: {n.Subject.LatestCommentUrl}");

            // Prior comment renders BEFORE the latest so chronological order on screen matches
            // the conversation. Labelled distinctly so the classifier can tell the two apart
            // — the latest comment is the one that fired the notification; the prior is
            // context, sometimes referenced by the latest ("see above", "yes do that").
            if (!string.IsNullOrEmpty(priorBody))
            {
                sb.AppendLine();
                var who = string.IsNullOrEmpty(priorAuthor) ? "" : $" by {priorAuthor}";
                sb.AppendLine($"#### Prior comment{who} (context — not what fired this notification)");
                sb.AppendLine("```");
                sb.AppendLine(priorBody);
                sb.AppendLine("```");
            }

            if (!string.IsNullOrEmpty(body))
            {
                sb.AppendLine();
                // Only label "Latest comment" when there's a prior comment to disambiguate
                // it from — otherwise the format stays identical to the pre-enrichment shape.
                if (!string.IsNullOrEmpty(priorBody))
                    sb.AppendLine("#### Latest comment (the one that fired this notification)");
                sb.AppendLine("```");
                sb.AppendLine(body);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Classify this branch's notifications per your system prompt and return the JSON verdict.");
        return sb.ToString();
    }

    static TriageVerdict DefaultActionable(string why)
        => TriageVerdict.Actionable(why, summary: "", confidence: 0.5);

    /// <summary>
    /// Strips GitHub quoted-reply lines (lines starting with <c>&gt;</c>) and the immediately
    /// following blank separator line, so L2 patterns don't match against quoted noise.
    /// </summary>
    public static string StripQuotedReplies(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return "";

        var sb = new StringBuilder(body.Length);
        var lines = body.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('>'))
            {
                while (i < lines.Length && lines[i].StartsWith('>'))
                    i++;
                if (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                if (i < lines.Length)
                    sb.AppendLine(lines[i]);
                continue;
            }
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

}

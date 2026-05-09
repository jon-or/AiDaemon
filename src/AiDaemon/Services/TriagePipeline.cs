using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    readonly Lazy<string> _systemPrompt = new(() => LoadEmbedded("system-prompt.md"));
    readonly Lazy<string> _schema = new(() => LoadEmbedded("schema.json"));
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

    public async Task<(TriageVerdict? Verdict, string CommentBody)> QuickTriageAsync(GhNotification n, CancellationToken cancellationToken)
    {
        // ---------- L1: author / type / reason / rate ----------
        if (!SupportedSubjectTypes.Contains(n.Subject.Type))
            return (TriageVerdict.Drop($"unsupported subject type: {n.Subject.Type}"), "");

        if (!_actionableReasons.Contains(n.Reason))
            return (TriageVerdict.Drop($"reason '{n.Reason}' not in ActionableReasons"), "");

        // L1 author check requires the comment body. Fetch once; the body is also returned
        // so callers can pass it through to the agent without re-fetching the same URL.
        CommentInfo? comment = null;
        if (!string.IsNullOrWhiteSpace(n.Subject.LatestCommentUrl))
            comment = await _gh.GetCommentAsync(n.Subject.LatestCommentUrl!, cancellationToken);

        var author = comment?.User.Login ?? "";
        var body = comment?.Body ?? "";

        if (!string.IsNullOrEmpty(_options.AiUserLogin) &&
            string.Equals(author, _options.AiUserLogin, StringComparison.OrdinalIgnoreCase))
            return (TriageVerdict.Drop($"self-authored by {author}"), body);

        if (!string.IsNullOrEmpty(author) && _botBlocklist.Contains(author))
            return (TriageVerdict.Drop($"blocklisted bot author: {author}"), body);

        // Rate-limit check is the last L1 step so we don't burn budget on notifications that
        // would have been dropped above.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var newCount = await _store.IncrementRateLimitAsync(n.Id, today, cancellationToken);
        if (newCount > _options.Triage.MaxActionsPerThreadPerDay)
        {
            _logger.LogInformation(
                "rate-limit drop thread={ThreadId} count={Count} max={Max}",
                n.Id, newCount, _options.Triage.MaxActionsPerThreadPerDay);
            return (TriageVerdict.Drop(
                $"thread daily rate limit ({newCount}/{_options.Triage.MaxActionsPerThreadPerDay})"), body);
        }

        // ---------- L2: regex content filter ----------
        var stripped = StripQuotedReplies(body);
        foreach (var pat in _l2Patterns.Value)
        {
            if (pat.IsMatch(stripped))
                return (TriageVerdict.Drop($"L2 regex match: /{pat}/"), body);
        }

        // Defer to agent triage.
        return (null, body);
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

        // Use the most-recent notification's title for fallback summary text.
        var primary = items.OrderByDescending(i => i.Notification.UpdatedAt).First();

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
        catch (OperationCanceledException)
        {
            // Daemon shutdown — abort the tick rather than fabricating a fallback verdict.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "agent triage failed branch={Branch} count={Count} — defaulting to actionable",
                branch.Key, items.Count);
            return TriageVerdict.Actionable(
                $"agent error ({ex.GetType().Name}) — actionable by default",
                summary: TruncateSummary(primary.Notification.Subject.Title),
                confidence: 0.5);
        }

        if (result.IsError)
        {
            _logger.LogWarning(
                "agent triage is_error=true branch={Branch} count={Count} result={Result} — defaulting to actionable",
                branch.Key, items.Count, result.Result);
            return TriageVerdict.Actionable(
                "agent reported is_error=true — actionable by default",
                summary: TruncateSummary(primary.Notification.Subject.Title),
                confidence: 0.5);
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
            return TriageVerdict.Actionable(
                "agent returned no structured output — actionable by default",
                summary: TruncateSummary(primary.Notification.Subject.Title),
                confidence: 0.5);
        }

        // Trust the agent's verdict directly — no post-hoc bias rule.
        var action = string.Equals(llm.Action, "drop", StringComparison.OrdinalIgnoreCase)
            ? TriageAction.Drop
            : TriageAction.Actionable;

        var verdict = new TriageVerdict(action, llm.Why, llm.Summary, llm.Confidence);

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

            sb.AppendLine();
            sb.AppendLine($"### {i + 1}. {n.Subject.Type} — reason `{n.Reason}` — {n.UpdatedAt:O}");
            sb.AppendLine($"- Title: {n.Subject.Title}");
            if (!string.IsNullOrEmpty(n.Subject.LatestCommentUrl))
                sb.AppendLine($"- Latest comment URL: {n.Subject.LatestCommentUrl}");

            if (!string.IsNullOrEmpty(body))
            {
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(body);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Classify this branch's notifications per your system prompt and return the JSON verdict.");
        return sb.ToString();
    }

    static string TruncateSummary(string s)
        => string.IsNullOrEmpty(s) ? "(no summary)" : (s.Length <= 200 ? s : s[..200]);

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

    static string LoadEmbedded(string fileName)
    {
        var asm = typeof(TriagePipeline).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource {fileName} not found. Check AiDaemon.csproj <EmbeddedResource> entries.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

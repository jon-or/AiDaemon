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

    public async Task<TriageVerdict?> QuickTriageAsync(GhNotification n, CancellationToken cancellationToken)
    {
        // ---------- L1: author / type / reason / rate ----------
        if (!SupportedSubjectTypes.Contains(n.Subject.Type))
            return TriageVerdict.Drop($"unsupported subject type: {n.Subject.Type}");

        if (!_actionableReasons.Contains(n.Reason))
            return TriageVerdict.Drop($"reason '{n.Reason}' not in ActionableReasons");

        // L1 author check requires the comment body. Fetch once and reuse for L2 below.
        CommentInfo? comment = null;
        if (!string.IsNullOrWhiteSpace(n.Subject.LatestCommentUrl))
            comment = await _gh.GetCommentAsync(n.Subject.LatestCommentUrl!, cancellationToken);

        var author = comment?.User.Login ?? "";
        var body = comment?.Body ?? "";

        if (!string.IsNullOrEmpty(_options.AiUserLogin) &&
            string.Equals(author, _options.AiUserLogin, StringComparison.OrdinalIgnoreCase))
            return TriageVerdict.Drop($"self-authored by {author}");

        if (!string.IsNullOrEmpty(author) && _botBlocklist.Contains(author))
            return TriageVerdict.Drop($"blocklisted bot author: {author}");

        // Rate-limit check is the last L1 step so we don't burn budget on notifications that
        // would have been dropped above.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var newCount = await _store.IncrementRateLimitAsync(n.Id, today, cancellationToken);
        if (newCount > _options.Triage.MaxActionsPerThreadPerDay)
        {
            _logger.LogInformation(
                "rate-limit drop thread={ThreadId} count={Count} max={Max}",
                n.Id, newCount, _options.Triage.MaxActionsPerThreadPerDay);
            return TriageVerdict.Drop(
                $"thread daily rate limit ({newCount}/{_options.Triage.MaxActionsPerThreadPerDay})");
        }

        // ---------- L2: regex content filter ----------
        var stripped = StripQuotedReplies(body);
        foreach (var pat in _l2Patterns.Value)
        {
            if (pat.IsMatch(stripped))
                return TriageVerdict.Drop($"L2 regex match: /{pat}/");
        }

        // Defer to agent triage.
        return null;
    }

    public async Task<TriageVerdict> AgentTriageAsync(GhNotification n, BranchInfo branch, CancellationToken cancellationToken)
    {
        var sid = Guid.NewGuid().ToString();
        var userInput = BuildAgentInput(n, branch);

        ClaudeJsonResult result;
        try
        {
            result = await _claude.RunHeadlessJsonAsync(
                systemPrompt: _systemPrompt.Value,
                userInput: userInput,
                schemaJson: _schema.Value,
                model: _options.Triage.Model,
                workingDirectory: branch.Worktree,
                timeout: L3Timeout,
                cancellationToken: cancellationToken,
                sessionId: sid,
                permissionMode: "bypassPermissions");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "agent triage failed thread={ThreadId} branch={Branch} — defaulting to actionable",
                n.Id, branch.Key);
            return TriageVerdict.Actionable(
                $"agent error ({ex.GetType().Name}) — actionable by default",
                summary: TruncateSummary(n.Subject.Title),
                confidence: 0.5,
                sessionId: sid);
        }

        if (result.IsError)
        {
            _logger.LogWarning(
                "agent triage is_error=true thread={ThreadId} branch={Branch} result={Result} — defaulting to actionable",
                n.Id, branch.Key, result.Result);
            return TriageVerdict.Actionable(
                "agent reported is_error=true — actionable by default",
                summary: TruncateSummary(n.Subject.Title),
                confidence: 0.5,
                sessionId: sid);
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
                _logger.LogWarning(ex, "agent triage produced unparseable structured_output thread={ThreadId}", n.Id);
            }
        }

        if (llm == null)
        {
            _logger.LogWarning(
                "agent triage returned no structured output thread={ThreadId} — defaulting to actionable", n.Id);
            return TriageVerdict.Actionable(
                "agent returned no structured output — actionable by default",
                summary: TruncateSummary(n.Subject.Title),
                confidence: 0.5,
                sessionId: sid);
        }

        var verdict = ApplyAsymmetricBias(llm, BodyForBiasCheck(n));

        // Log the verdict + agent's session info. The agent's actual research/fix work lives
        // in the JSONL transcript inside ~/.claude/projects/<encoded-cwd>/<sid>.jsonl and is
        // what the user sees when they take over via RC.
        _logger.LogInformation(
            "L3 verdict thread={ThreadId} branch={Branch} sid={Sid} action={Action} confidence={Confidence:F2} why={Why}",
            n.Id, branch.Key, sid, verdict.Action, verdict.Confidence, verdict.Why);

        return verdict with { SessionId = sid };
    }

    /// <summary>
    /// Honor an L3 "drop" only when the model is confident AND the body has no <c>?</c> AND
    /// no @-mention of the user. Otherwise upgrade to actionable. False-drop is strictly
    /// worse than false-actionable for this user.
    /// </summary>
    public TriageVerdict ApplyAsymmetricBias(TriageStructuredOutput llm, string fullBody)
    {
        var requestedDrop = string.Equals(llm.Action, "drop", StringComparison.OrdinalIgnoreCase);
        if (!requestedDrop)
            return new TriageVerdict(TriageAction.Actionable, llm.Why, llm.Summary, llm.Confidence);

        var hasQuestion = fullBody.Contains('?');
        var atMention = !string.IsNullOrEmpty(_options.AiUserLogin) &&
            Regex.IsMatch(fullBody, $@"@{Regex.Escape(_options.AiUserLogin)}\b", RegexOptions.IgnoreCase);

        if (llm.Confidence >= 0.8 && !hasQuestion && !atMention)
            return new TriageVerdict(TriageAction.Drop, llm.Why, llm.Summary, llm.Confidence);

        var upgradeReason = (llm.Confidence < 0.8 ? "low-confidence" : "")
            + (hasQuestion ? " has-question" : "")
            + (atMention ? " at-mention" : "");
        return new TriageVerdict(
            TriageAction.Actionable,
            $"upgraded from drop ({upgradeReason.Trim()}): {llm.Why}",
            llm.Summary,
            llm.Confidence);
    }

    /// <summary>
    /// Bias check looks at the comment body for <c>?</c> and @-mentions. We re-fetch the body
    /// at agent-triage time to keep this self-contained — the QuickTriage call doesn't return
    /// the body, and the agent may already have the content from its own tool use anyway.
    /// </summary>
    static string BodyForBiasCheck(GhNotification n)
        => n.Subject.Title ?? "";

    /// <summary>The user message the agent receives. Includes everything it needs to investigate.</summary>
    static string BuildAgentInput(GhNotification n, BranchInfo branch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# GitHub notification");
        sb.AppendLine($"- Repository: {n.Repository.FullName}");
        sb.AppendLine($"- Type: {n.Subject.Type}");
        sb.AppendLine($"- Reason: {n.Reason}");
        sb.AppendLine($"- Title: {n.Subject.Title}");
        if (branch.PrNumber is int pr) sb.AppendLine($"- PR: #{pr}");
        if (branch.IssueNumber is int issue) sb.AppendLine($"- Issue: #{issue}");
        sb.AppendLine($"- Branch: {branch.Branch}");
        sb.AppendLine($"- Worktree (your cwd): {branch.Worktree}");
        if (!string.IsNullOrEmpty(n.Subject.LatestCommentUrl))
        {
            sb.AppendLine($"- Latest comment URL (use `gh api` if you need the body): {n.Subject.LatestCommentUrl}");
        }
        sb.AppendLine();
        sb.AppendLine("Triage this notification per your system prompt. If actionable, do meaningful prep using your tools, then return the JSON verdict.");
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

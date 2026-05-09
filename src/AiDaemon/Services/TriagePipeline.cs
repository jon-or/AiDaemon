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

    /// <summary>Per-call timeout for the L3 claude invocation.</summary>
    static readonly TimeSpan L3Timeout = TimeSpan.FromSeconds(30);

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

    public async Task<TriageVerdict> TriageAsync(GhNotification n, CancellationToken cancellationToken)
    {
        // ---------- L1: author / type / reason / rate ----------
        if (!SupportedSubjectTypes.Contains(n.Subject.Type))
            return TriageVerdict.Drop($"unsupported subject type: {n.Subject.Type}");

        if (!_actionableReasons.Contains(n.Reason))
            return TriageVerdict.Drop($"reason '{n.Reason}' not in ActionableReasons");

        // review_requested has no comment body to evaluate. Skip L2/L3.
        if (string.Equals(n.Reason, "review_requested", StringComparison.OrdinalIgnoreCase))
            return TriageVerdict.Actionable("review_requested", $"Review requested: {n.Subject.Title}");

        // L1 author check requires the comment body. Fetch it once and reuse.
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

        // ---------- L3: LLM ----------
        if (string.IsNullOrWhiteSpace(stripped))
        {
            // No body to feed L3. Default to actionable per asymmetric bias.
            _logger.LogInformation(
                "L3 skipped (empty body) thread={ThreadId} reason={Reason}", n.Id, n.Reason);
            return TriageVerdict.Actionable("empty body — defaulted to actionable");
        }

        TriageStructuredOutput? llm;
        try
        {
            llm = await CallL3Async(stripped, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "L3 call failed thread={ThreadId} — defaulting to actionable", n.Id);
            return TriageVerdict.Actionable($"L3 error ({ex.GetType().Name}) — actionable by default");
        }

        if (llm == null)
        {
            _logger.LogWarning(
                "L3 returned no structured output thread={ThreadId} — defaulting to actionable", n.Id);
            return TriageVerdict.Actionable("L3 returned no structured output — actionable by default");
        }

        var verdict = ApplyAsymmetricBias(llm, body);

        // Audit trail: log the full body on every verdict so we can spot-check L3 drops later.
        _logger.LogInformation(
            "L3 verdict thread={ThreadId} action={Action} confidence={Confidence:F2} why={Why} body={Body}",
            n.Id, verdict.Action, verdict.Confidence, verdict.Why, body);

        return verdict;
    }

    /// <summary>
    /// Honor an L3 "drop" only when the model is confident AND the body has no `?` AND no
    /// @-mention of the user. Otherwise upgrade to actionable. False-drop is strictly worse
    /// than false-actionable for this user.
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

    async Task<TriageStructuredOutput?> CallL3Async(string commentBody, CancellationToken cancellationToken)
    {
        // Use a stable scratch dir under DataDir so claude's project state doesn't pollute
        // real worktrees and gets cached across calls.
        var cwd = Path.Combine(_options.DataDir, "triage-scratch");

        var result = await _claude.RunHeadlessJsonAsync(
            systemPrompt: _systemPrompt.Value,
            userInput: commentBody,
            schemaJson: _schema.Value,
            model: _options.Triage.Model,
            workingDirectory: cwd,
            timeout: L3Timeout,
            cancellationToken: cancellationToken);

        if (result.IsError)
            throw new InvalidOperationException($"claude reported is_error=true: {result.Result}");

        if (result.StructuredOutput is null)
            return null;

        return JsonSerializer.Deserialize<TriageStructuredOutput>(
            result.StructuredOutput.Value.GetRawText(),
            JsonOpts);
    }

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
                // Eat consecutive quoted lines, then skip an immediately-following blank line.
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

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class AgentPreRunner : IAgentPreRunner
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Hard wall-clock cap on the pre-run. Bigger than triage's classifier cap because the agent does multi-turn tool use.</summary>
    static readonly TimeSpan PreRunTimeout = TimeSpan.FromMinutes(10);

    readonly IClaudeRunner _claude;
    readonly DaemonOptions _options;
    readonly ILogger<AgentPreRunner> _logger;

    readonly Lazy<string> _systemPrompt = new(() =>
        EmbeddedResource.Load(typeof(AgentPreRunner).Assembly, "pre-run-prompt.md"));

    readonly Lazy<string> _schema = new(() =>
        EmbeddedResource.Load(typeof(AgentPreRunner).Assembly, "pre-run-schema.json"));

    public AgentPreRunner(
        IClaudeRunner claude,
        IOptions<DaemonOptions> options,
        ILogger<AgentPreRunner> logger)
    {
        _claude = claude;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PreRunResult> RunAsync(
        string sessionId,
        BranchInfo branch,
        IReadOnlyList<NotificationWithBody> items,
        TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            throw new ArgumentException("Pre-run requires at least one notification.", nameof(items));

        var userInput = BuildUserInput(branch, items, verdict);

        _logger.LogInformation(
            "pre-run starting sid={Sid} branch={Branch} model={Model} count={Count}",
            sessionId, branch.Key, _options.Triage.PreRunModel, items.Count);

        ClaudeJsonResult result;
        try
        {
            // --json-schema forces the agent's final assistant turn to be a JSON object
            // matching the schema. The user resuming via RC will see that JSON in the
            // transcript — acceptable because they've already read the structured summary
            // in the ntfy push and RC is for further driving, not first-look reading.
            result = await _claude.RunHeadlessJsonAsync(
                systemPrompt: _systemPrompt.Value,
                userInput: userInput,
                schemaJson: _schema.Value,
                model: _options.Triage.PreRunModel,
                workingDirectory: branch.Worktree,
                timeout: PreRunTimeout,
                cancellationToken: cancellationToken,
                sessionId: sessionId,
                permissionMode: "bypassPermissions");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "pre-run threw sid={Sid} branch={Branch} — proceeding to RC anyway",
                sessionId, branch.Key);
            return PreRunResult.Failed;
        }

        if (result.IsError)
        {
            _logger.LogWarning(
                "pre-run is_error=true sid={Sid} branch={Branch} result={Result}",
                sessionId, branch.Key, result.Result);
            return PreRunResult.Failed;
        }

        var summary = ExtractSummary(result, sessionId, branch);

        _logger.LogInformation(
            "pre-run completed sid={Sid} branch={Branch} duration_ms={Ms} summary={Summary}",
            sessionId, branch.Key, result.DurationMs, summary);

        return new PreRunResult(Succeeded: true, Summary: summary);
    }

    /// <summary>
    /// Pulls the <c>summary</c> field out of <c>structured_output</c>. Returns empty (rather
    /// than throwing) when the agent produced something the schema should have caught — at
    /// that point the dispatcher falls back to the triage verdict's summary for the push.
    /// </summary>
    string ExtractSummary(ClaudeJsonResult result, string sessionId, BranchInfo branch)
    {
        if (result.StructuredOutput is not JsonElement so)
        {
            _logger.LogWarning(
                "pre-run produced no structured_output sid={Sid} branch={Branch}",
                sessionId, branch.Key);
            return "";
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PreRunStructuredOutput>(so.GetRawText(), JsonOpts);
            return payload?.Summary?.Trim() ?? "";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "pre-run structured_output unparseable sid={Sid} branch={Branch}",
                sessionId, branch.Key);
            return "";
        }
    }

    static string BuildUserInput(BranchInfo branch, IReadOnlyList<NotificationWithBody> items, TriageVerdict verdict)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Branch: {branch.Branch}");
        sb.AppendLine($"- Repository: {branch.Repo}");
        if (branch.PrNumber is int pr) sb.AppendLine($"- PR: #{pr}");
        if (branch.IssueNumber is int issue) sb.AppendLine($"- Issue: #{issue}");
        sb.AppendLine($"- Worktree (your cwd): {branch.Worktree}");
        sb.AppendLine();

        sb.AppendLine("## Triage verdict");
        sb.AppendLine();
        sb.AppendLine($"- why: {verdict.Why}");
        sb.AppendLine();

        if (items.Count == 1)
            sb.AppendLine("## Notification");
        else
            sb.AppendLine($"## {items.Count} notifications on this branch");

        var ordered = items.OrderBy(i => i.Notification.UpdatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var n = ordered[i].Notification;
            var body = ordered[i].CommentBody;
            var author = ordered[i].CommentAuthor;
            var priorBody = ordered[i].PriorCommentBody;
            var priorAuthor = ordered[i].PriorCommentAuthor;

            sb.AppendLine();
            sb.AppendLine($"### {i + 1}. {n.Subject.Type} — reason `{n.Reason}` — {n.UpdatedAt:O}");
            sb.AppendLine($"- Title: {n.Subject.Title}");
            // Author is the GitHub user.login of whoever posted the comment. The system
            // prompt requires the agent to credit this user by name in its summary.
            sb.AppendLine($"- Author: {(string.IsNullOrEmpty(author) ? "(unknown)" : author)}");
            if (!string.IsNullOrEmpty(n.Subject.LatestCommentUrl))
                sb.AppendLine($"- Latest comment URL: {n.Subject.LatestCommentUrl}");

            // The pre-run gets the prior conversation comment as context. The pre-run agent's
            // job is to credit the *latest* commenter (the requester named above) — the prior
            // comment is reference material when the latest comment refers back to it.
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
                if (!string.IsNullOrEmpty(priorBody))
                    sb.AppendLine("#### Latest comment (the one that fired this notification)");
                sb.AppendLine("```");
                sb.AppendLine(body);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Do the prep work per your system prompt. End with a JSON object whose `summary` field credits the requester by name, describes what they asked for, and what you did.");
        return sb.ToString();
    }

    class PreRunStructuredOutput
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }
}

using System.Reflection;
using System.Text;
using AiDaemon.Configuration;
using AiDaemon.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class AgentPreRunner : IAgentPreRunner
{
    /// <summary>Hard wall-clock cap on the pre-run. Bigger than triage's classifier cap because the agent does multi-turn tool use.</summary>
    static readonly TimeSpan PreRunTimeout = TimeSpan.FromMinutes(10);

    readonly IClaudeRunner _claude;
    readonly DaemonOptions _options;
    readonly ILogger<AgentPreRunner> _logger;

    readonly Lazy<string> _systemPrompt = new(() => LoadEmbedded("pre-run-prompt.md"));

    public AgentPreRunner(
        IClaudeRunner claude,
        IOptions<DaemonOptions> options,
        ILogger<AgentPreRunner> logger)
    {
        _claude = claude;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RunAsync(
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

        try
        {
            // No --json-schema: the pre-run is a free-form agent run that ends with a text
            // summary the user reads first when they take over. We pass a permissive schema
            // to keep the response wrapper shape consistent with the triage call site.
            var result = await _claude.RunHeadlessJsonAsync(
                systemPrompt: _systemPrompt.Value,
                userInput: userInput,
                schemaJson: "{\"type\":\"object\"}",
                model: _options.Triage.PreRunModel,
                workingDirectory: branch.Worktree,
                timeout: PreRunTimeout,
                cancellationToken: cancellationToken,
                sessionId: sessionId,
                permissionMode: "bypassPermissions");

            if (result.IsError)
            {
                _logger.LogWarning(
                    "pre-run is_error=true sid={Sid} branch={Branch} result={Result}",
                    sessionId, branch.Key, result.Result);
                return false;
            }

            _logger.LogInformation(
                "pre-run completed sid={Sid} branch={Branch} duration_ms={Ms}",
                sessionId, branch.Key, result.DurationMs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "pre-run threw sid={Sid} branch={Branch} — proceeding to RC anyway",
                sessionId, branch.Key);
            return false;
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
        sb.AppendLine($"- summary: {verdict.Summary}");
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
        sb.AppendLine("Do the prep work per your system prompt. End with a brief text summary the user will read first.");
        return sb.ToString();
    }

    static string LoadEmbedded(string fileName)
    {
        var asm = typeof(AgentPreRunner).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource {fileName} not found. Check AiDaemon.csproj <EmbeddedResource> entries.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

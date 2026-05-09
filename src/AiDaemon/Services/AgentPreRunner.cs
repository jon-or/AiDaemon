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
    readonly IGhClient _gh;
    readonly DaemonOptions _options;
    readonly ILogger<AgentPreRunner> _logger;

    readonly Lazy<string> _systemPrompt = new(() => LoadEmbedded("pre-run-prompt.md"));

    public AgentPreRunner(
        IClaudeRunner claude,
        IGhClient gh,
        IOptions<DaemonOptions> options,
        ILogger<AgentPreRunner> logger)
    {
        _claude = claude;
        _gh = gh;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RunAsync(
        string sessionId,
        BranchInfo branch,
        GhNotification n,
        TriageVerdict verdict,
        CancellationToken cancellationToken)
    {
        // Pre-fetch the comment body once so the agent has it inline (no tool round-trip just
        // to read GitHub).
        CommentInfo? comment = null;
        if (!string.IsNullOrWhiteSpace(n.Subject.LatestCommentUrl))
        {
            try
            {
                comment = await _gh.GetCommentAsync(n.Subject.LatestCommentUrl!, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "pre-run: failed to pre-fetch comment body — agent can fetch via tools");
            }
        }
        var commentBody = comment?.Body ?? "";

        var userInput = BuildUserInput(n, branch, verdict, commentBody);

        _logger.LogInformation(
            "pre-run starting sid={Sid} branch={Branch} model={Model}",
            sessionId, branch.Key, _options.Triage.PreRunModel);

        try
        {
            // No --json-schema: the pre-run is a free-form agent run that ends with a text
            // summary the user reads first when they take over. We pass an empty schema string
            // by using --output-format json without a schema to keep the response wrapper
            // shape consistent with the triage call site.
            var result = await _claude.RunHeadlessJsonAsync(
                systemPrompt: _systemPrompt.Value,
                userInput: userInput,
                schemaJson: "{\"type\":\"object\"}",  // permissive: any object passes
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
                // Still return true — partial progress may have made it to disk and is worth
                // showing the user.
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

    static string BuildUserInput(GhNotification n, BranchInfo branch, TriageVerdict verdict, string commentBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GitHub notification — triage classified this as actionable");
        sb.AppendLine($"- Repository: {n.Repository.FullName}");
        sb.AppendLine($"- Type: {n.Subject.Type}");
        sb.AppendLine($"- Reason: {n.Reason}");
        sb.AppendLine($"- Title: {n.Subject.Title}");
        if (branch.PrNumber is int pr) sb.AppendLine($"- PR: #{pr}");
        if (branch.IssueNumber is int issue) sb.AppendLine($"- Issue: #{issue}");
        sb.AppendLine($"- Branch: {branch.Branch}");
        sb.AppendLine($"- Worktree (your cwd): {branch.Worktree}");
        if (!string.IsNullOrEmpty(n.Subject.LatestCommentUrl))
            sb.AppendLine($"- Latest comment URL: {n.Subject.LatestCommentUrl}");

        sb.AppendLine();
        sb.AppendLine("## Triage verdict");
        sb.AppendLine();
        sb.AppendLine($"- summary: {verdict.Summary}");
        sb.AppendLine($"- why: {verdict.Why}");

        if (!string.IsNullOrEmpty(commentBody))
        {
            sb.AppendLine();
            sb.AppendLine("## Comment body");
            sb.AppendLine();
            sb.AppendLine(commentBody);
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

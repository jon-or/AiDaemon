namespace AiDaemon.Services;

public interface IClaudeRunner
{
    /// <summary>
    /// Invokes <c>claude -p --model &lt;model&gt; --output-format json --json-schema &lt;schema&gt;
    /// --system-prompt &lt;prompt&gt; &lt;userInput&gt;</c> and returns the parsed wrapper plus the
    /// structured payload as a <see cref="System.Text.Json.JsonElement"/>.
    /// </summary>
    /// <param name="systemPrompt">The system-prompt string passed via <c>--system-prompt</c>.</param>
    /// <param name="userInput">The user-message argument (the comment body, etc.).</param>
    /// <param name="schemaJson">The JSON schema string for <c>--json-schema</c>.</param>
    /// <param name="model">Model alias (e.g. "haiku") for <c>--model</c>.</param>
    /// <param name="workingDirectory">Cwd for the subprocess. Pick a stable scratch dir to avoid polluting real worktrees.</param>
    /// <param name="timeout">Per-call timeout. The process tree is killed on expiry.</param>
    Task<ClaudeJsonResult> RunHeadlessJsonAsync(
        string systemPrompt,
        string userInput,
        string schemaJson,
        string model,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal projection of the <c>claude -p --output-format json</c> response wrapper.
/// </summary>
public record ClaudeJsonResult(
    bool IsError,
    string? Result,
    System.Text.Json.JsonElement? StructuredOutput,
    string? StopReason,
    int DurationMs);

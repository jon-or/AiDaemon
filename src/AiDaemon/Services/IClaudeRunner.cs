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
    /// <param name="workingDirectory">Cwd for the subprocess.</param>
    /// <param name="timeout">Per-call timeout. The process tree is killed on expiry.</param>
    /// <param name="sessionId">When non-null, the call uses <c>--session-id &lt;sessionId&gt;</c> and persists the conversation JSONL so a future <c>claude --resume</c> can pick it up. When null, <c>--no-session-persistence</c> is set so the call leaves no JSONL on disk.</param>
    /// <param name="permissionMode">Optional <c>--permission-mode</c> value (e.g. <c>"bypassPermissions"</c>) — required if <paramref name="userInput"/> is expected to drive tool use.</param>
    Task<ClaudeJsonResult> RunHeadlessJsonAsync(
        string systemPrompt,
        string userInput,
        string schemaJson,
        string model,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? sessionId = null,
        string? permissionMode = null);
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

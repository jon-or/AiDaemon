using System.Text.Json;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class ClaudeRunner : IClaudeRunner
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly IProcessRunner _runner;
    readonly DaemonOptions _options;
    readonly ILogger<ClaudeRunner> _logger;

    public ClaudeRunner(IProcessRunner runner, IOptions<DaemonOptions> options, ILogger<ClaudeRunner> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClaudeJsonResult> RunHeadlessJsonAsync(
        string systemPrompt,
        string userInput,
        string schemaJson,
        string model,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? sessionId = null,
        string? permissionMode = null)
    {
        Directory.CreateDirectory(workingDirectory);

        // --bare requires ANTHROPIC_API_KEY (no OAuth/keychain reads). Most users run on the
        // Claude Pro/Max OAuth login, so we use the standard --print path. The plan documents
        // this fallback.
        var args = new List<string>
        {
            "-p",
            "--model", model,
            "--output-format", "json",
            "--json-schema", schemaJson,
            "--system-prompt", systemPrompt,
        };

        if (!string.IsNullOrEmpty(sessionId))
        {
            args.Add("--session-id");
            args.Add(sessionId);
        }
        else
        {
            args.Add("--no-session-persistence");
        }

        if (!string.IsNullOrEmpty(permissionMode))
        {
            args.Add("--permission-mode");
            args.Add(permissionMode);
        }

        args.Add(userInput);

        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perCallCts.CancelAfter(timeout);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                _options.ClaudePath,
                args,
                workingDirectory: workingDirectory,
                cancellationToken: perCallCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("claude -p exceeded {TimeoutSec}s; killed process tree", timeout.TotalSeconds);
            throw new TimeoutException($"claude -p exceeded {timeout.TotalSeconds:N0}s");
        }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"claude -p failed (exit {result.ExitCode}): {result.Stderr.TruncateWithEllipsis(500)}");
        }

        // The wrapper is one JSON object on stdout. With --output-format=json there's no streaming.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"claude -p stdout was not valid JSON: {ex.Message}. First 200 chars: {result.Stdout.TruncateWithEllipsis(200)}",
                ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            return new ClaudeJsonResult(
                IsError: root.TryGetProperty("is_error", out var isErr) && isErr.GetBoolean(),
                Result: root.TryGetProperty("result", out var r) ? r.GetString() : null,
                StructuredOutput: root.TryGetProperty("structured_output", out var so) ? so.Clone() : null,
                StopReason: root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null,
                DurationMs: root.TryGetProperty("duration_ms", out var d) && d.TryGetInt32(out var dms) ? dms : 0);
        }
    }

}

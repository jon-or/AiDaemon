using AiDaemon.Configuration;
using AiDaemon.Process;
using AiDaemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests.Services;

public class ClaudeRunnerTests
{
    readonly Mock<IProcessRunner> _runner = new(MockBehavior.Strict);
    readonly DaemonOptions _options = new() { ClaudePath = "claude" };

    ClaudeRunner Build() => new(_runner.Object, Options.Create(_options), NullLogger<ClaudeRunner>.Instance);

    /// <summary>Mocks IProcessRunner with a successful claude -p response and captures the args & stdin.</summary>
    void StubClaude(string stdout, out List<string> capturedArgs, out string? capturedStdin)
    {
        var args = new List<string>();
        string? stdin = null;
        _runner.Setup(r => r.RunAsync(
                "claude",
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<string> a, string? _, IReadOnlyDictionary<string, string?>? _, string? si, CancellationToken _) =>
            {
                args.AddRange(a);
                stdin = si;
            })
            .ReturnsAsync(new ProcessResult(0, stdout, ""));
        capturedArgs = args;
        capturedStdin = null;
        // Re-bind via closure; the actual values populate during RunAsync.
        // Using out-vars at the call site is awkward in C#, so reopen this in a helper:
    }

    static string OkWrapper(string structured = "{\"action\":\"actionable\",\"why\":\"x\"}")
        => $"{{\"is_error\":false,\"result\":\"done\",\"structured_output\":{structured},\"stop_reason\":\"end_turn\",\"duration_ms\":1234}}";

    [Fact]
    public async Task RunHeadlessJson_PipesUserInputViaStdin_NotArgv()
    {
        // Long inputs (the BuildAgentInput payload with every comment body inline) blow
        // past Windows CreateProcess's 32k argv limit. Switching to stdin avoids that.
        List<string>? captured = null;
        string? capturedStdin = null;
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<string> a, string? _, IReadOnlyDictionary<string, string?>? _, string? si, CancellationToken _) =>
            { captured = a.ToList(); capturedStdin = si; })
            .ReturnsAsync(new ProcessResult(0, OkWrapper(), ""));

        var bigInput = new string('x', 50_000); // would exceed CreateProcess limit if on argv
        await Build().RunHeadlessJsonAsync(
            systemPrompt: "sys",
            userInput: bigInput,
            schemaJson: "{}",
            model: "haiku",
            workingDirectory: Path.GetTempPath(),
            timeout: TimeSpan.FromSeconds(60),
            cancellationToken: default,
            sessionId: null,
            permissionMode: null);

        Assert.NotNull(captured);
        // The 50k user input is on stdin, not argv.
        Assert.Equal(bigInput, capturedStdin);
        Assert.DoesNotContain(captured!, a => a == bigInput);
    }

    [Fact]
    public async Task RunHeadlessJson_ArgvShape_MatchesContract()
    {
        List<string>? captured = null;
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<string> a, string? _, IReadOnlyDictionary<string, string?>? _, string? _, CancellationToken _) =>
                captured = a.ToList())
            .ReturnsAsync(new ProcessResult(0, OkWrapper(), ""));

        await Build().RunHeadlessJsonAsync(
            systemPrompt: "SYS-MARKER",
            userInput: "USER-MARKER",
            schemaJson: "{\"type\":\"object\"}",
            model: "haiku",
            workingDirectory: Path.GetTempPath(),
            timeout: TimeSpan.FromSeconds(60),
            cancellationToken: default,
            sessionId: "deadbeef-1234",
            permissionMode: "bypassPermissions");

        Assert.NotNull(captured);
        Assert.Equal("-p", captured![0]);
        Assert.Contains("--model", captured); Assert.Contains("haiku", captured);
        Assert.Contains("--output-format", captured); Assert.Contains("json", captured);
        Assert.Contains("--json-schema", captured); Assert.Contains("{\"type\":\"object\"}", captured);
        Assert.Contains("--system-prompt", captured); Assert.Contains("SYS-MARKER", captured);
        Assert.Contains("--session-id", captured); Assert.Contains("deadbeef-1234", captured);
        Assert.Contains("--permission-mode", captured); Assert.Contains("bypassPermissions", captured);
        Assert.DoesNotContain("--no-session-persistence", captured); // mutually exclusive with --session-id
    }

    [Fact]
    public async Task RunHeadlessJson_NullSessionId_AddsNoSessionPersistence()
    {
        List<string>? captured = null;
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<string> a, string? _, IReadOnlyDictionary<string, string?>? _, string? _, CancellationToken _) =>
                captured = a.ToList())
            .ReturnsAsync(new ProcessResult(0, OkWrapper(), ""));

        await Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), default,
            sessionId: null, permissionMode: null);

        Assert.NotNull(captured);
        Assert.Contains("--no-session-persistence", captured);
        Assert.DoesNotContain("--session-id", captured!);
        Assert.DoesNotContain("--permission-mode", captured!); // omitted when null
    }

    [Fact]
    public async Task RunHeadlessJson_InnerTimeoutFires_ThrowsTimeoutException_NotOCE()
    {
        // The per-call CancelAfter wraps OperationCanceledException as TimeoutException
        // when the *inner* deadline fires (and the caller's outer token is still alive).
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<TimeoutException>(() => Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task RunHeadlessJson_OuterCancellationFires_PropagatesAsOCE()
    {
        // When the *caller* cancels (not the inner timer) we must not swallow the OCE —
        // the host expects to see cancellation propagate so shutdown is prompt.
        using var outerCts = new CancellationTokenSource();
        outerCts.Cancel();
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(outerCts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), outerCts.Token));
    }

    [Fact]
    public async Task RunHeadlessJson_NonZeroExit_ThrowsInvalidOperationWithStderrTruncated()
    {
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(2, "", "auth required: run claude /login"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), default));
        Assert.Contains("exit 2", ex.Message);
        Assert.Contains("auth required", ex.Message);
    }

    [Fact]
    public async Task RunHeadlessJson_MalformedStdout_ThrowsInvalidOperation_PreservesJsonExceptionAsInner()
    {
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "not json at all", ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), default));
        // JsonReaderException : JsonException — accept the base type for forward-compat.
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Contains("First 200 chars", ex.Message);
    }

    [Fact]
    public async Task RunHeadlessJson_HappyPath_ProjectsAllFields_AndStructuredOutputSurvivesAcrossDocDispose()
    {
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0,
                """{"is_error":false,"result":"finished","structured_output":{"action":"actionable","confidence":0.85,"why":"needs review"},"stop_reason":"end_turn","duration_ms":4321}""",
                ""));

        var got = await Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), default);

        Assert.False(got.IsError);
        Assert.Equal("finished", got.Result);
        Assert.Equal("end_turn", got.StopReason);
        Assert.Equal(4321, got.DurationMs);

        // structured_output is cloned during parsing so the caller can read it after the
        // backing JsonDocument has been disposed (see ClaudeRunner.cs ~line 111).
        Assert.NotNull(got.StructuredOutput);
        Assert.Equal("actionable", got.StructuredOutput!.Value.GetProperty("action").GetString());
        Assert.Equal(0.85, got.StructuredOutput.Value.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task RunHeadlessJson_IsErrorTrue_StillReturnsResult_NotThrows()
    {
        _runner.Setup(r => r.RunAsync(
                "claude", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0,
                """{"is_error":true,"result":"Not logged in","duration_ms":50}""",
                ""));

        var got = await Build().RunHeadlessJsonAsync(
            "sys", "user", "{}", "haiku",
            Path.GetTempPath(), TimeSpan.FromSeconds(60), default);

        // is_error=true is a payload signal, not a hard exception — callers (TriagePipeline,
        // AgentPreRunner) inspect it and decide policy. The runner must not throw here.
        Assert.True(got.IsError);
        Assert.Equal("Not logged in", got.Result);
        Assert.Null(got.StructuredOutput);
    }
}

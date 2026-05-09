using AiDaemon.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiDaemon.Tests.Process;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsStdoutAndZeroExit_OnSuccess()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo hello" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExit_OnFailure()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync(
            "cmd.exe",
            new[] { "/c", "exit 7" });

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_PassesEnvironment()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo %AI_DAEMON_TEST%" },
            environment: new Dictionary<string, string?> { ["AI_DAEMON_TEST"] = "marker-value" });

        Assert.Contains("marker-value", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_KillsTreeAndThrows_OnCancel()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await runner.RunAsync(
                "cmd.exe",
                new[] { "/c", "ping -n 30 127.0.0.1 > nul" },
                cancellationToken: cts.Token);
        });
    }
}

using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SysProcess = System.Diagnostics.Process;

namespace AiDaemon.Tests.Services;

/// <summary>
/// Pure-function and registry-parsing coverage for <see cref="RcLauncher"/>. The
/// cmd.exe spawn / WMI flow itself is integration territory; this file pins the boundary
/// behaviors that don't need a real process.
/// </summary>
public class RcLauncherTests
{
    readonly Mock<IFileSystem> _fs = new();
    readonly DaemonOptions _options = new() { ClaudePath = "claude" };

    RcLauncher BuildLauncher() => new(_fs.Object, Options.Create(_options), NullLogger<RcLauncher>.Instance);

    static string RegistryPathFor(int pid) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "sessions", $"{pid}.json");

    /// <summary>Use the current test process so PID + StartTime are real and stable.</summary>
    static (int Pid, long StartTicks) Self()
    {
        var p = SysProcess.GetCurrentProcess();
        return (p.Id, p.StartTime.Ticks);
    }

    [Theory]
    [InlineData("16119-isdpvirtualproperty", "16119-isdpvirtualproperty")]
    // Slashes preserved — feature branches commonly use them.
    [InlineData("feature/foo", "feature/foo")]
    // Dots and underscores allowed.
    [InlineData("release/v1.2.3", "release/v1.2.3")]
    [InlineData("a_b", "a_b")]
    // Shell-special characters get replaced with '-'.
    [InlineData("16119; rm -rf /", "16119--rm--rf-/")]
    [InlineData("foo&bar", "foo-bar")]
    [InlineData("foo|bar", "foo-bar")]
    [InlineData("foo`evil`", "foo-evil")]
    [InlineData("$(whoami)", "whoami")]
    [InlineData("foo'bar", "foo-bar")]
    [InlineData("foo\"bar", "foo-bar")]
    [InlineData("foo bar", "foo-bar")]
    [InlineData("foo>bar<baz", "foo-bar-baz")]
    // Empty / all-stripped fall back to a stable default.
    [InlineData("", "ai-daemon")]
    [InlineData("$$$", "ai-daemon")]
    public void SafeRcName_StripsShellSpecialsViaAllowlist(string input, string expected)
    {
        Assert.Equal(expected, RcLauncher.SafeRcName(input));
    }

    // ---------- IsAliveAsync registry-read contract ----------

    [Fact]
    public async Task IsAlive_PidNotRunning_ReturnsFalse_WithoutRegistryRead()
    {
        // Pick a PID that's almost certainly not assigned: int.MaxValue.
        var got = await BuildLauncher().IsAliveAsync(int.MaxValue, 1, default);
        Assert.False(got);
        // Process check short-circuited; no FileExists / ReadAllText needed.
        _fs.Verify(f => f.FileExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IsAlive_PidRecycled_StartTimeMismatch_ReturnsFalse()
    {
        // Same PID, wrong start ticks — defends against a freshly-spawned process
        // happening to land on the same PID we recorded for an old one.
        var (pid, _) = Self();
        var got = await BuildLauncher().IsAliveAsync(pid, claudeStartTicks: 0L, default);
        Assert.False(got);
    }

    [Fact]
    public async Task IsAlive_RegistryFileMissing_ReturnsFalse()
    {
        var (pid, ticks) = Self();
        _fs.Setup(f => f.FileExists(RegistryPathFor(pid))).Returns(false);
        var got = await BuildLauncher().IsAliveAsync(pid, ticks, default);
        Assert.False(got);
    }

    [Fact]
    public async Task IsAlive_RegistryHasValidBridge_ReturnsTrue()
    {
        var (pid, ticks) = Self();
        _fs.Setup(f => f.FileExists(RegistryPathFor(pid))).Returns(true);
        _fs.Setup(f => f.ReadAllText(RegistryPathFor(pid)))
            .Returns("""{"bridgeSessionId":"session_01ABC","sessionId":"sid-1"}""");
        var got = await BuildLauncher().IsAliveAsync(pid, ticks, default);
        Assert.True(got);
    }

    [Fact]
    public async Task IsAlive_RegistryHasEmptyBridgeSessionId_ReturnsFalse()
    {
        // Bridge tore down — the relay outage scenario. Process can be alive but the
        // session is dead and we should reap on the next sweep.
        var (pid, ticks) = Self();
        _fs.Setup(f => f.FileExists(RegistryPathFor(pid))).Returns(true);
        _fs.Setup(f => f.ReadAllText(RegistryPathFor(pid)))
            .Returns("""{"bridgeSessionId":"","sessionId":"sid-1"}""");
        var got = await BuildLauncher().IsAliveAsync(pid, ticks, default);
        Assert.False(got);
    }

    [Fact]
    public async Task IsAlive_RegistryReadIOExceptionThenSucceeds_ReturnsTrue_AfterRetry()
    {
        // claude.exe rewrites the registry file in place when bridge state changes; a
        // read landing mid-write produces an IOException. The launcher retries once
        // after 100ms — without that, the dispatcher would reap a healthy RC because
        // we caught the file in flux.
        var (pid, ticks) = Self();
        _fs.Setup(f => f.FileExists(RegistryPathFor(pid))).Returns(true);
        var calls = 0;
        _fs.Setup(f => f.ReadAllText(RegistryPathFor(pid)))
            .Returns(() => ++calls switch
            {
                1 => throw new IOException("file in flux"),
                _ => """{"bridgeSessionId":"session_01ABC","sessionId":"sid-1"}""",
            });
        var got = await BuildLauncher().IsAliveAsync(pid, ticks, default);
        Assert.True(got);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IsAlive_RegistryReadFailsTwice_ReturnsFalse()
    {
        // Second read still fails: treat the session as dead. Without the cap, we'd
        // hang forever on a permanently-corrupted file.
        var (pid, ticks) = Self();
        _fs.Setup(f => f.FileExists(RegistryPathFor(pid))).Returns(true);
        _fs.Setup(f => f.ReadAllText(RegistryPathFor(pid)))
            .Throws(new System.Text.Json.JsonException("malformed"));
        var got = await BuildLauncher().IsAliveAsync(pid, ticks, default);
        Assert.False(got);
        _fs.Verify(f => f.ReadAllText(RegistryPathFor(pid)), Times.Exactly(2));
    }
}

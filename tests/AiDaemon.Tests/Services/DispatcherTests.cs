using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests.Services;

public class DispatcherTests : IDisposable
{
    readonly string _dbPath;
    readonly SqliteStateStore _store;
    readonly Mock<IRcLauncher> _launcher = new();
    readonly Mock<INotificationPusher> _pusher = new();
    readonly Mock<IFileSystem> _fs = new();
    readonly DaemonOptions _options = new()
    {
        DataDir = Path.GetTempPath(),
        WorktreeRoot = @"D:\git\orez.worktrees",
        RcIdleTimeoutHours = 2,
    };

    readonly Mock<IAgentPreRunner> _preRunner = new();

    Dispatcher Build() => new(
        _launcher.Object,
        _preRunner.Object,
        _pusher.Object,
        _store,
        _fs.Object,
        Options.Create(_options),
        NullLogger<Dispatcher>.Instance);

    public DispatcherTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dispatcher-tests-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _store = new SqliteStateStore(connStr, NullLogger<SqliteStateStore>.Instance);
        _store.InitializeAsync(default).GetAwaiter().GetResult();

        // Default: pre-run succeeds (returns true). Individual tests can override.
        _preRunner.Setup(p => p.RunAsync(
                It.IsAny<string>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }

    static BranchInfo Branch(string branch = "16119-isdpvirtualproperty", int? issue = 16119)
        => new("ownerrez/orez", branch, $@"D:\git\orez.worktrees\{branch}", PrNumber: null, IssueNumber: issue);

    static GhNotification N() => new()
    {
        Id = "thread-1",
        Reason = "mention",
        Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        Subject = new GhNotificationSubject { Type = "Issue", Title = "fix the thing" },
    };

    static TriageVerdict V() => TriageVerdict.Actionable("test", "fix the thing");

    static RcAttachment Att(int psPid = 1234, int claudePid = 5678, long startTicks = 1_000_000_000_000L,
        string bridge = "session_01ABC", string url = "https://claude.ai/code/session_01ABC",
        string sessionId = "00000000-1111-2222-3333-444444444444")
        => new(psPid, claudePid, startTicks, bridge, url, sessionId);

    [Fact]
    public async Task FirstEvent_NoSessionId_RunsPreRunThenSpawnsRcWithSameSid()
    {
        var branch = Branch();
        string? capturedPreRunSid = null;
        string? capturedSpawnSid = null;

        _preRunner.Setup(p => p.RunAsync(It.IsAny<string>(), branch, It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()))
            .Callback((string sid, BranchInfo _, IReadOnlyList<NotificationWithBody> _, TriageVerdict _, CancellationToken _) => capturedPreRunSid = sid)
            .ReturnsAsync(true);

        _launcher.Setup(l => l.SpawnRcAsync(branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((BranchInfo _, string? sid, CancellationToken _) => capturedSpawnSid = sid)
            .ReturnsAsync((BranchInfo _, string? sid, CancellationToken _) => Att(sessionId: sid ?? "(none)"));

        var outcome = await Build().DispatchAsync(branch, new[] { new NotificationWithBody(N(), "body") }, V(), default);

        Assert.Equal(DispatchOutcome.Spawned, outcome);
        Assert.NotNull(capturedPreRunSid);
        Assert.Equal(capturedPreRunSid, capturedSpawnSid);  // same sid threaded through both steps

        _pusher.Verify(p => p.PushSessionLinkAsync(
            It.IsAny<string>(), branch, It.IsAny<GhNotification>(), It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _pusher.Verify(p => p.PushHeadsUpAsync(
            It.IsAny<string>(), It.IsAny<BranchInfo>(), It.IsAny<GhNotification>(), It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.NotNull(rec);
        Assert.Equal(BranchMode.RcActive, rec!.Mode);
        Assert.Equal(capturedPreRunSid, rec.SessionId);
        Assert.Equal(1234, rec.RcPid);
        Assert.Equal(5678, rec.RcClaudePid);
        Assert.Equal(16119, rec.IssueNumber);
    }

    [Fact]
    public async Task SecondEvent_RcAlive_EmitsHeadsUpOnly_NoSpawn()
    {
        var branch = Branch();
        var sid = "fixed-sid";

        // Pre-seed an active state.
        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = branch.Key,
            SessionId = sid,
            Worktree = branch.Worktree,
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 1_000_000_000_000L,
            RcBridgeId = "session_01ABC",
            RcUrl = "https://claude.ai/code/session_01ABC",
            LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100,
            IssueNumber = 16119,
        }, default);

        _launcher.Setup(l => l.IsAlive(5678, 1_000_000_000_000L)).Returns(true);

        var outcome = await Build().DispatchAsync(branch, new[] { new NotificationWithBody(N(), "body") }, V(), default);

        Assert.Equal(DispatchOutcome.HeadsUp, outcome);
        _launcher.Verify(l => l.SpawnRcAsync(It.IsAny<BranchInfo>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Never);
        _pusher.Verify(p => p.PushHeadsUpAsync(
            "https://claude.ai/code/session_01ABC", branch, It.IsAny<GhNotification>(), It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // last_event_at advanced.
        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.True(rec!.LastEventAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5);
    }

    [Fact]
    public async Task SecondEvent_RcDead_CleansUpAndRespawns_PreservesSessionId()
    {
        var branch = Branch();
        var sid = "fixed-sid";

        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = branch.Key,
            SessionId = sid,
            Worktree = branch.Worktree,
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 1_000_000_000_000L,
            RcBridgeId = "old_bridge",
            RcUrl = "https://claude.ai/code/old_bridge",
            LastEventAt = 0,
            IssueNumber = 16119,
        }, default);

        _launcher.Setup(l => l.IsAlive(5678, 1_000_000_000_000L)).Returns(false);
        _launcher.Setup(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _launcher.Setup(l => l.SpawnRcAsync(branch, sid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Att(psPid: 9999, claudePid: 8888, startTicks: 2_000_000_000_000L,
                bridge: "session_NEW", url: "https://claude.ai/code/session_NEW", sessionId: sid));

        var outcome = await Build().DispatchAsync(branch, new[] { new NotificationWithBody(N(), "body") }, V(), default);

        Assert.Equal(DispatchOutcome.Spawned, outcome);
        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Once);
        // SessionId preserved — respawn passes the existing sid, not null.
        _launcher.Verify(l => l.SpawnRcAsync(branch, sid, It.IsAny<CancellationToken>()), Times.Once);

        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.Equal(sid, rec!.SessionId);
        Assert.Equal(8888, rec.RcClaudePid);
        Assert.Equal("session_NEW", rec.RcBridgeId);
        Assert.Equal("https://claude.ai/code/session_NEW", rec.RcUrl);
    }

    [Fact]
    public async Task SpawnFailure_ReturnsFailed_NoPush_PreservesPreRunSessionId()
    {
        var branch = Branch();
        // Spawn fails for any sid the dispatcher generates.
        _launcher.Setup(l => l.SpawnRcAsync(branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PowerShell never spawned claude.exe"));

        var outcome = await Build().DispatchAsync(branch, new[] { new NotificationWithBody(N(), "body") }, V(), default);

        Assert.Equal(DispatchOutcome.Failed, outcome);
        _pusher.Verify(p => p.PushSessionLinkAsync(
            It.IsAny<string>(), It.IsAny<BranchInfo>(), It.IsAny<GhNotification>(), It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The branch row exists in Idle and persists the sid we ran pre-run against, so the
        // next dispatch can reuse it without re-running pre-run.
        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.NotNull(rec);
        Assert.Equal(BranchMode.Idle, rec!.Mode);
        Assert.False(string.IsNullOrEmpty(rec.SessionId), "sid generated for pre-run is preserved");
    }

    [Fact]
    public async Task Sweep_ReapsDeadRow_PreservesSessionId()
    {
        var branch = Branch();
        var sid = "fixed-sid";

        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = branch.Key,
            SessionId = sid,
            Worktree = branch.Worktree,
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 1_000_000_000_000L,
            RcBridgeId = "x",
            RcUrl = "https://claude.ai/code/x",
            LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, default);

        _launcher.Setup(l => l.IsAlive(5678, 1_000_000_000_000L)).Returns(false);
        _launcher.Setup(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Build().SweepAsync(default);

        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Once);
        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.Equal(BranchMode.Idle, rec!.Mode);
        Assert.Null(rec.RcPid);
        Assert.Null(rec.RcClaudePid);
        Assert.Equal(sid, rec.SessionId); // preserved
    }

    [Fact]
    public async Task Sweep_ReapsIdleTimeout_BasedOnJsonlMtime()
    {
        var branch = Branch();
        var sid = "fixed-sid";

        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = branch.Key,
            SessionId = sid,
            Worktree = branch.Worktree,
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 1_000_000_000_000L,
            RcBridgeId = "x",
            RcUrl = "https://claude.ai/code/x",
            LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, default);

        _launcher.Setup(l => l.IsAlive(5678, 1_000_000_000_000L)).Returns(true);
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fs.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>()))
            .Returns(DateTime.UtcNow.AddHours(-3)); // older than 2h timeout

        _launcher.Setup(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Build().SweepAsync(default);

        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Once);
        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.Equal(BranchMode.Idle, rec!.Mode);
        Assert.Equal(sid, rec.SessionId);
    }

    [Fact]
    public async Task Sweep_DoesNotReap_WhenAliveAndJsonlFresh()
    {
        var branch = Branch();

        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = branch.Key,
            SessionId = "fixed-sid",
            Worktree = branch.Worktree,
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 1_000_000_000_000L,
            RcBridgeId = "x",
            RcUrl = "https://claude.ai/code/x",
            LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, default);

        _launcher.Setup(l => l.IsAlive(5678, 1_000_000_000_000L)).Returns(true);
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fs.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>())).Returns(DateTime.UtcNow.AddMinutes(-30));

        await Build().SweepAsync(default);

        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Never);
        var rec = await _store.GetBranchStateAsync(branch.Key, default);
        Assert.Equal(BranchMode.RcActive, rec!.Mode);
    }

    [Fact]
    public async Task ReconcileOnStartup_ResetsAllRcActiveRowsToIdle_PreservesSessionIds()
    {
        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = "ownerrez/orez:foo",
            SessionId = "sid-foo",
            Worktree = @"D:\git\orez.worktrees\foo",
            Mode = BranchMode.RcActive,
            RcPid = 100,
            RcClaudePid = 200,
            RcClaudeStart = 999_000_000_000L,
            RcBridgeId = "x",
            RcUrl = "https://claude.ai/code/x",
            LastEventAt = 0,
        }, default);
        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = "ownerrez/orez:bar",
            SessionId = "sid-bar",
            Worktree = @"D:\git\orez.worktrees\bar",
            Mode = BranchMode.RcActive,
            RcPid = 300,
            RcClaudePid = 400,
            RcClaudeStart = 888_000_000_000L,
            RcBridgeId = "y",
            RcUrl = "https://claude.ai/code/y",
            LastEventAt = 0,
        }, default);

        await Build().ReconcileOnStartupAsync(default);

        var foo = await _store.GetBranchStateAsync("ownerrez/orez:foo", default);
        var bar = await _store.GetBranchStateAsync("ownerrez/orez:bar", default);

        Assert.Equal(BranchMode.Idle, foo!.Mode);
        Assert.Null(foo.RcPid);
        Assert.Equal("sid-foo", foo.SessionId);

        Assert.Equal(BranchMode.Idle, bar!.Mode);
        Assert.Null(bar.RcPid);
        Assert.Equal("sid-bar", bar.SessionId);

        // Reconciliation must NOT call CleanupAsync — those PIDs are dead by definition.
        _launcher.Verify(l => l.CleanupAsync(It.IsAny<BranchState>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

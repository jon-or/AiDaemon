using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiDaemon.Tests.Storage;

public class SqliteStateStoreTests : IDisposable
{
    readonly string _dbPath;
    readonly SqliteStateStore _store;

    public SqliteStateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ai-daemon-tests-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _store = new SqliteStateStore(connStr, NullLogger<SqliteStateStore>.Instance);
        _store.InitializeAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        // SQLite holds the file open via the shared cache; collect to release before delete.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await _store.InitializeAsync(default);
        await _store.InitializeAsync(default);

        // No throws == pass. Marker write also confirms tables exist.
        await _store.MarkProcessedAsync("t", "c", "seen", default);
        Assert.True(await _store.IsProcessedAsync("t", "c", default));
    }

    [Fact]
    public async Task MarkAndIs_RoundTripPerCommentKey()
    {
        Assert.False(await _store.IsProcessedAsync("thread-1", "c1", default));

        await _store.MarkProcessedAsync("thread-1", "c1", "seen", default);

        Assert.True(await _store.IsProcessedAsync("thread-1", "c1", default));
        Assert.False(await _store.IsProcessedAsync("thread-1", "c2", default));
        Assert.False(await _store.IsProcessedAsync("thread-2", "c1", default));
    }

    [Fact]
    public async Task MarkProcessed_OnConflictOverwritesOutcome()
    {
        await _store.MarkProcessedAsync("t", "c", "seen", default);
        await _store.MarkProcessedAsync("t", "c", "escalated", default);

        Assert.True(await _store.IsProcessedAsync("t", "c", default));
    }

    [Fact]
    public async Task PruneProcessed_RemovesOldRowsOnly()
    {
        await _store.MarkProcessedAsync("t", "old", "seen", default);

        // Cutoff in the future deletes everything written so far.
        var pruned = await _store.PruneProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal(1, pruned);
        Assert.False(await _store.IsProcessedAsync("t", "old", default));

        await _store.MarkProcessedAsync("t", "fresh", "seen", default);
        var prunedNothing = await _store.PruneProcessedAsync(DateTimeOffset.UtcNow.AddDays(-1), default);
        Assert.Equal(0, prunedNothing);
        Assert.True(await _store.IsProcessedAsync("t", "fresh", default));
    }

    [Fact]
    public async Task BranchState_RoundTripsAllFields()
    {
        var state = new BranchState
        {
            Branch = "ownerrez/orez:412-fix-x",
            SessionId = "00000000-1111-2222-3333-444444444444",
            Worktree = @"C:\Users\Jon\worktrees\412-fix-x",
            Mode = BranchMode.RcActive,
            RcPid = 1234,
            RcClaudePid = 5678,
            RcClaudeStart = 638_000_000_000_000_000L,
            RcBridgeId = "session_01ABC",
            RcUrl = "https://claude.ai/code/session_01ABC",
            LastEventAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PrNumber = 412,
            IssueNumber = null,
        };

        await _store.UpsertBranchStateAsync(state, default);

        var got = await _store.GetBranchStateAsync(state.Branch, default);
        Assert.NotNull(got);
        Assert.Equal(state.Branch, got!.Branch);
        Assert.Equal(state.SessionId, got.SessionId);
        Assert.Equal(state.Worktree, got.Worktree);
        Assert.Equal(state.Mode, got.Mode);
        Assert.Equal(state.RcPid, got.RcPid);
        Assert.Equal(state.RcClaudePid, got.RcClaudePid);
        Assert.Equal(state.RcClaudeStart, got.RcClaudeStart);
        Assert.Equal(state.RcBridgeId, got.RcBridgeId);
        Assert.Equal(state.RcUrl, got.RcUrl);
        Assert.Equal(state.LastEventAt, got.LastEventAt);
        Assert.Equal(state.PrNumber, got.PrNumber);
        Assert.Null(got.IssueNumber);
    }

    [Fact]
    public async Task BranchState_UpsertOverwrites()
    {
        var state = new BranchState
        {
            Branch = "ownerrez/orez:412-fix-x",
            SessionId = "sid-1",
            Worktree = "wt",
            Mode = BranchMode.Idle,
            LastEventAt = 100,
        };
        await _store.UpsertBranchStateAsync(state, default);

        state.Mode = BranchMode.RcActive;
        state.RcPid = 7;
        state.LastEventAt = 200;
        await _store.UpsertBranchStateAsync(state, default);

        var got = await _store.GetBranchStateAsync(state.Branch, default);
        Assert.Equal(BranchMode.RcActive, got!.Mode);
        Assert.Equal(7, got.RcPid);
        Assert.Equal(200, got.LastEventAt);
    }

    [Fact]
    public async Task ListActiveBranches_ReturnsOnlyRcActive()
    {
        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = "r:idle",
            SessionId = "s",
            Worktree = "w",
            Mode = BranchMode.Idle,
            LastEventAt = 1,
        }, default);
        await _store.UpsertBranchStateAsync(new BranchState
        {
            Branch = "r:active",
            SessionId = "s",
            Worktree = "w",
            Mode = BranchMode.RcActive,
            LastEventAt = 2,
        }, default);

        var active = await _store.ListActiveBranchesAsync(default);
        Assert.Single(active);
        Assert.Equal("r:active", active[0].Branch);
    }

    [Fact]
    public async Task RateLimit_IncrementIsAtomic()
    {
        var day = new DateOnly(2026, 5, 9);

        Assert.Equal(0, await _store.GetRateLimitAsync("t", day, default));

        Assert.Equal(1, await _store.IncrementRateLimitAsync("t", day, default));
        Assert.Equal(2, await _store.IncrementRateLimitAsync("t", day, default));
        Assert.Equal(3, await _store.IncrementRateLimitAsync("t", day, default));
        Assert.Equal(3, await _store.GetRateLimitAsync("t", day, default));

        // Different day starts fresh.
        Assert.Equal(1, await _store.IncrementRateLimitAsync("t", day.AddDays(1), default));
        Assert.Equal(3, await _store.GetRateLimitAsync("t", day, default));
    }
}

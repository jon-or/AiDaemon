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
        await _store.MarkProcessedAsync("t", "c", "seen", null, default);
        Assert.True(await _store.IsProcessedAsync("t", "c", default));
    }

    [Fact]
    public async Task MarkAndIs_RoundTripPerCommentKey()
    {
        Assert.False(await _store.IsProcessedAsync("thread-1", "c1", default));

        await _store.MarkProcessedAsync("thread-1", "c1", "seen", null, default);

        Assert.True(await _store.IsProcessedAsync("thread-1", "c1", default));
        Assert.False(await _store.IsProcessedAsync("thread-1", "c2", default));
        Assert.False(await _store.IsProcessedAsync("thread-2", "c1", default));
    }

    [Fact]
    public async Task MarkProcessed_OnConflictOverwritesOutcome()
    {
        await _store.MarkProcessedAsync("t", "c", "seen", null, default);
        await _store.MarkProcessedAsync("t", "c", "escalated", null, default);

        Assert.True(await _store.IsProcessedAsync("t", "c", default));
    }

    [Fact]
    public async Task PruneProcessed_RemovesOldRowsOnly()
    {
        await _store.MarkProcessedAsync("t", "old", "seen", null, default);

        // Cutoff in the future deletes everything written so far.
        var pruned = await _store.PruneProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal(1, pruned);
        Assert.False(await _store.IsProcessedAsync("t", "old", default));

        await _store.MarkProcessedAsync("t", "fresh", "seen", null, default);
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
    public async Task MarkProcessed_PersistsDisplayContextForRetryMenu()
    {
        var ctx = new ProcessedContext("ownerrez/orez", "Fix the things", "PullRequest");
        await _store.MarkProcessedAsync("t1", "c1", "spawned:k", ctx, default);

        var rows = await _store.ListRecentProcessedAsync(10, default);
        Assert.Single(rows);
        Assert.Equal("ownerrez/orez", rows[0].Repo);
        Assert.Equal("Fix the things", rows[0].Title);
        Assert.Equal("PullRequest", rows[0].SubjectType);
        Assert.Equal("spawned:k", rows[0].Outcome);
    }

    [Fact]
    public async Task MarkProcessed_NullContextLeavesDisplayColumnsNull()
    {
        // The poller's dedup pre-check writes no context (it doesn't process the notification,
        // just notes "seen"); the row should still load without the Retry menu choking.
        await _store.MarkProcessedAsync("t1", "c1", "seen", null, default);

        var rows = await _store.ListRecentProcessedAsync(10, default);
        Assert.Single(rows);
        Assert.Null(rows[0].Repo);
        Assert.Null(rows[0].Title);
        Assert.Null(rows[0].SubjectType);
    }

    [Fact]
    public async Task MarkProcessed_ConflictUpsertKeepsExistingContextWhenNewIsNull()
    {
        // First write carries context (the L1/L2 path always has it). A hypothetical second
        // write that doesn't (defensive — no current call site does this) must not blank out
        // the repo/title we'll need for the Retry submenu after the row has aged.
        var ctx = new ProcessedContext("ownerrez/orez", "Title", "Issue");
        await _store.MarkProcessedAsync("t1", "c1", "first", ctx, default);
        await _store.MarkProcessedAsync("t1", "c1", "second", null, default);

        var rows = await _store.ListRecentProcessedAsync(10, default);
        Assert.Single(rows);
        Assert.Equal("ownerrez/orez", rows[0].Repo);
        Assert.Equal("second", rows[0].Outcome);
    }

    [Fact]
    public async Task ListRecentProcessed_ReturnsNewestFirstAndRespectsLimit()
    {
        // Inserts are ordered in time; verify newest-first ordering survives the round trip.
        await _store.MarkProcessedAsync("t", "c1", "first",  null, default);
        await Task.Delay(1100); // processed_at is unix seconds — push past the boundary.
        await _store.MarkProcessedAsync("t", "c2", "second", null, default);
        await Task.Delay(1100);
        await _store.MarkProcessedAsync("t", "c3", "third",  null, default);

        var top2 = await _store.ListRecentProcessedAsync(2, default);
        Assert.Equal(2, top2.Count);
        Assert.Equal("third",  top2[0].Outcome);
        Assert.Equal("second", top2[1].Outcome);

        var none = await _store.ListRecentProcessedAsync(0, default);
        Assert.Empty(none);
    }

    [Fact]
    public async Task UnmarkProcessed_DeletesRowAndReturnsTrue_OnlyOnce()
    {
        await _store.MarkProcessedAsync("t", "c", "seen", null, default);
        Assert.True(await _store.IsProcessedAsync("t", "c", default));

        var first = await _store.UnmarkProcessedAsync("t", "c", default);
        Assert.True(first);
        Assert.False(await _store.IsProcessedAsync("t", "c", default));

        // Second unmark on the same key is a no-op and reports it as such — the tray's retry
        // path uses the return value purely for diagnostic logging, but it has to be accurate.
        var second = await _store.UnmarkProcessedAsync("t", "c", default);
        Assert.False(second);
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

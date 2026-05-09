using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AiDaemon.Tests.Services;

public class NotificationPollerTests : IDisposable
{
    readonly string _dbPath;
    readonly SqliteStateStore _store;
    readonly Mock<IGhClient> _gh = new(MockBehavior.Strict);
    readonly NotificationPoller _poller;

    public NotificationPollerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"poller-tests-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _store = new SqliteStateStore(connStr, NullLogger<SqliteStateStore>.Instance);
        _store.InitializeAsync(default).GetAwaiter().GetResult();
        _poller = new NotificationPoller(_gh.Object, _store, NullLogger<NotificationPoller>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }

    static GhNotification N(string id, string? commentUrl, string reason = "mention",
        DateTimeOffset? updated = null) => new()
    {
        Id = id,
        Reason = reason,
        Unread = true,
        UpdatedAt = updated ?? DateTimeOffset.UtcNow,
        Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        Subject = new GhNotificationSubject
        {
            Title = $"Thread {id}",
            Type = "Issue",
            Url = $"https://api.github.com/repos/ownerrez/orez/issues/{id}",
            LatestCommentUrl = commentUrl,
        },
    };

    async Task<List<GhNotification>> CollectAsync()
    {
        var list = new List<GhNotification>();
        await foreach (var n in _poller.PollAsync(default))
            list.Add(n);
        return list;
    }

    [Fact]
    public async Task PollAsync_FirstRun_AnchorsCursorAtNow_AndYieldsNothing()
    {
        // First poll with no cursor: poller writes 'now' as the cursor before fetching, then
        // calls gh with that cursor. We assert it asked gh with a non-null since.
        DateTimeOffset? capturedSince = null;
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTimeOffset? since, CancellationToken _) => capturedSince = since)
            .ReturnsAsync(Array.Empty<GhNotification>());

        var got = await CollectAsync();

        Assert.Empty(got);
        Assert.NotNull(capturedSince);
        var stored = await _store.GetKvAsync(StateStoreKeys.NotificationCursor, default);
        Assert.False(string.IsNullOrEmpty(stored));
    }

    [Fact]
    public async Task PollAsync_YieldsAllUnseenOnFirstPass()
    {
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                N("1", "https://api.github.com/repos/o/r/issues/comments/100"),
                N("2", "https://api.github.com/repos/o/r/issues/comments/200"),
            });

        var got = await CollectAsync();

        Assert.Equal(2, got.Count);
        Assert.Equal("1", got[0].Id);
        Assert.Equal("2", got[1].Id);
    }

    [Fact]
    public async Task PollAsync_SkipsAlreadyProcessed()
    {
        var commentUrl = "https://api.github.com/repos/o/r/issues/comments/100";
        await _store.MarkProcessedAsync("1", "100", "seen", default);

        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { N("1", commentUrl), N("2", null) });

        var got = await CollectAsync();

        Assert.Single(got);
        Assert.Equal("2", got[0].Id);
    }

    [Fact]
    public void DeriveCommentId_UsesUrlLastSegment_OrUpdatedAtSentinel()
    {
        Assert.Equal(
            "100",
            NotificationPoller.DeriveCommentId(N("t", "https://api.github.com/repos/o/r/issues/comments/100")));

        var ts = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.Equal(
            "updated:1700000000",
            NotificationPoller.DeriveCommentId(N("t", null, updated: ts)));

        Assert.Equal(
            "updated:1700000000",
            NotificationPoller.DeriveCommentId(N("t", "", updated: ts)));
    }

    [Fact]
    public async Task PollAsync_WithSameThreadDifferentComment_YieldsAgain()
    {
        await _store.MarkProcessedAsync("1", "100", "seen", default);

        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                N("1", "https://api.github.com/repos/o/r/issues/comments/200"),
            });

        var got = await CollectAsync();

        Assert.Single(got);
        Assert.Equal("1", got[0].Id);
    }

    [Fact]
    public async Task PollAsync_AdvancesCursorPastMaxUpdatedAt()
    {
        // Seed an old cursor so the test's mock data (also old) is "newer" than the cursor
        // and the advance path actually runs.
        var seeded = DateTimeOffset.UtcNow.AddDays(-1);
        await _store.SetKvAsync(StateStoreKeys.NotificationCursor, seeded.ToString("O"), default);

        var t0 = seeded.AddMinutes(10);
        var latest = t0.AddMinutes(2);

        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                N("1", null, updated: t0),
                N("2", null, updated: latest),
                N("3", null, updated: t0.AddMinutes(1)),
            });

        await CollectAsync();

        var stored = await _store.GetKvAsync(StateStoreKeys.NotificationCursor, default);
        var parsed = DateTimeOffset.Parse(stored!).ToUniversalTime();
        Assert.True(parsed > latest, $"cursor {parsed:O} should be past latest {latest:O}");
        Assert.True(parsed <= latest.AddSeconds(2), $"cursor {parsed:O} should be at most ~1s past latest {latest:O}");
    }

    [Fact]
    public async Task PollAsync_PassesPersistedCursorToGh()
    {
        var seeded = DateTimeOffset.UtcNow.AddDays(-1);
        await _store.SetKvAsync(StateStoreKeys.NotificationCursor, seeded.ToString("O"), default);

        DateTimeOffset? captured = null;
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTimeOffset? since, CancellationToken _) => captured = since)
            .ReturnsAsync(Array.Empty<GhNotification>());

        await CollectAsync();

        Assert.NotNull(captured);
        Assert.Equal(seeded.ToUnixTimeSeconds(), captured.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task PollAsync_AuthFailure_YieldsNothingDoesNotThrow()
    {
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GhAuthException(1, "HTTP 401"));

        var got = await CollectAsync();

        Assert.Empty(got);
    }
}

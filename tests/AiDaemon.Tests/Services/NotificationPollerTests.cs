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
    public async Task PollAsync_YieldsAllUnseenOnFirstPass()
    {
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<CancellationToken>()))
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

        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { N("1", commentUrl), N("2", null) });

        var got = await CollectAsync();

        Assert.Single(got);
        Assert.Equal("2", got[0].Id);
    }

    [Fact]
    public async Task DeriveCommentId_UsesUrlLastSegment_OrUpdatedAtSentinel()
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
        // First comment seen, second comment on the same thread should still fire.
        await _store.MarkProcessedAsync("1", "100", "seen", default);

        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                N("1", "https://api.github.com/repos/o/r/issues/comments/200"),
            });

        var got = await CollectAsync();

        Assert.Single(got);
        Assert.Equal("1", got[0].Id);
    }

    [Fact]
    public async Task PollAsync_AuthFailure_YieldsNothingDoesNotThrow()
    {
        _gh.Setup(g => g.ListNotificationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GhAuthException(1, "HTTP 401"));

        var got = await CollectAsync();

        Assert.Empty(got);
    }
}

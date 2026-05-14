using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AiDaemon.Tests.Services;

/// <summary>
/// Focused coverage for <see cref="NotificationProcessor"/> — the entry point the tray
/// Retry path uses. The poll path inside Worker has its own batching + metrics; this
/// service is single-notification end-to-end. The invariants worth pinning:
///   - L1/L2 Drop, unresolved, L3 Drop, and dispatch failure each mark processed with
///     the same outcome-string vocabulary the poll path uses (so an operator inspecting
///     state.db sees one consistent set of strings).
///   - Rate-limit budget is NEVER charged for retries (this is the manual override).
///   - ProcessedContext.From(notification) is captured at every MarkProcessedAsync call.
/// </summary>
public class NotificationProcessorTests
{
    readonly Mock<IStateStore> _store = new(MockBehavior.Strict);
    readonly Mock<ITriagePipeline> _triage = new(MockBehavior.Strict);
    readonly Mock<IBranchResolver> _resolver = new(MockBehavior.Strict);
    readonly Mock<IDispatcher> _dispatcher = new(MockBehavior.Strict);

    public NotificationProcessorTests()
    {
        // Prior-comment enrichment is a best-effort step between branch-resolve and L3.
        // The behavior itself is exercised in TriagePipelineTests; here we just need the
        // strict-mode call to succeed when the test path reaches L3. Default: passthrough.
        // Registered in the constructor (not Build()) so individual tests can override it
        // and the override doesn't get clobbered when Build() re-registers later.
        _triage.Setup(t => t.EnrichWithPriorCommentsAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NotificationWithBody> items, BranchInfo _, CancellationToken _) => items);
    }

    NotificationProcessor Build() => new(
        _store.Object,
        _triage.Object,
        _resolver.Object,
        _dispatcher.Object,
        NullLogger<NotificationProcessor>.Instance);

    static GhNotification N(string id = "thread-A", string? commentUrl = null) => new()
    {
        Id = id,
        Reason = "mention",
        Unread = true,
        UpdatedAt = DateTimeOffset.UtcNow,
        Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        Subject = new GhNotificationSubject
        {
            Title = $"Title for {id}",
            Type = "Issue",
            Url = $"https://api.github.com/repos/o/r/issues/{id}",
            LatestCommentUrl = commentUrl ?? "https://api.github.com/repos/o/r/issues/comments/1",
        },
    };

    static BranchInfo SomeBranch() => new(
        "ownerrez/orez", "16119-isdpvirtualproperty",
        @"D:\git\orez.worktrees\16119-isdpvirtualproperty",
        PrNumber: null, IssueNumber: 16119);

    void ExpectMarkProcessed(string thread, string outcomeStartsWith, ProcessedContext? expectedContext = null)
    {
        // The Verify side asserts the exact outcome prefix + captured context shape; the Setup
        // here just needs to satisfy MockBehavior.Strict and return CompletedTask.
        _store.Setup(s => s.MarkProcessedAsync(
                thread,
                It.IsAny<string>(),
                It.Is<string>(o => o.StartsWith(outcomeStartsWith)),
                It.Is<ProcessedContext?>(c =>
                    expectedContext == null || (c != null && c.Repo == expectedContext.Repo && c.Title == expectedContext.Title)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ProcessOne_QuickTriageDrops_MarksDroppedAndReturnsDropped_NoResolveOrDispatch()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)TriageVerdict.Drop("L1 unsupported"), "", ""));
        ExpectMarkProcessed("thread-A", "dropped:L1 unsupported", ProcessedContext.From(n));

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Dropped, outcome);
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<BranchInfo>(), It.IsAny<IReadOnlyList<NotificationWithBody>>(),
            It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessOne_QuickTriageThrows_MarksFailedWithExceptionType()
    {
        // Exactly mirrors the poll path's defensive marker: if quick triage explodes we still
        // need a row in the processed table so the operator can see it in SQLite.
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("gh hiccup"));
        ExpectMarkProcessed("thread-A", "failed:quick-triage:InvalidOperationException");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Failed, outcome);
    }

    [Fact]
    public async Task ProcessOne_ResolverReturnsNull_MarksUnresolved()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BranchInfo?)null);
        ExpectMarkProcessed("thread-A", "unresolved");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Unresolved, outcome);
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<BranchInfo>(), It.IsAny<IReadOnlyList<NotificationWithBody>>(),
            It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessOne_AgentTriageDrops_MarksDroppedAgent_AndSkipsDispatch()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SomeBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Drop("noise"));
        ExpectMarkProcessed("thread-A", "dropped:agent:noise");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Dropped, outcome);
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<BranchInfo>(), It.IsAny<IReadOnlyList<NotificationWithBody>>(),
            It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessOne_HappyPath_DispatchesAndMarksSpawned_AndPassesSingleItem()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SomeBranch());

        IReadOnlyList<NotificationWithBody>? capturedItems = null;
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<NotificationWithBody> items, BranchInfo _, CancellationToken _) =>
                capturedItems = items)
            .ReturnsAsync(TriageVerdict.Actionable("go", "summary", 0.9));

        _dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchOutcome.Spawned);

        ExpectMarkProcessed("thread-A", "spawned:");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Spawned, outcome);
        Assert.NotNull(capturedItems);
        Assert.Single(capturedItems!); // Retry always sees a 1-item batch — no coalescing.
        Assert.Same(n, capturedItems![0].Notification);
        Assert.Equal("body", capturedItems[0].CommentBody);
        Assert.Equal("alice", capturedItems[0].CommentAuthor);
    }

    [Fact]
    public async Task ProcessOne_DispatchFailed_MarksFailedDispatch_AndReturnsFailed()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SomeBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Actionable("go", "x", 0.9));
        _dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchOutcome.Failed);
        ExpectMarkProcessed("thread-A", "failed:dispatch:");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.Failed, outcome);
    }

    [Fact]
    public async Task ProcessOne_DispatchHeadsUp_MarksHeadsUp_AndReturnsHeadsUp()
    {
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SomeBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Actionable("go", "x", 0.9));
        _dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchOutcome.HeadsUp);
        ExpectMarkProcessed("thread-A", "heads-up:");

        var outcome = await Build().ProcessOneAsync(n, default);

        Assert.Equal(RetryOutcome.HeadsUp, outcome);
    }

    [Fact]
    public async Task ProcessOne_NeverChargesRateLimit()
    {
        // Strict mock with no rate-limit setup proves the call is never made; the Verify
        // is belt-and-braces for anyone reading the test.
        var n = N();
        _triage.Setup(t => t.QuickTriageAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(n, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SomeBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Actionable("go", "x", 0.9));
        _dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchOutcome.Spawned);
        ExpectMarkProcessed("thread-A", "spawned:");

        await Build().ProcessOneAsync(n, default);

        _store.Verify(
            s => s.IncrementRateLimitAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

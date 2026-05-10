using System.Diagnostics.Metrics;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests;

/// <summary>
/// Coverage for <see cref="Worker.TickAsync"/> — the orchestration spine. The two-pass
/// shape (poll → L1/L2 → resolve → group → L3 → dispatch per branch) and the rate-limit /
/// MarkProcessed invariants are easy to break with a refactor; the unit tests on individual
/// services don't catch wiring bugs.
/// </summary>
public class WorkerTests
{
    readonly Mock<INotificationPoller> _poller = new(MockBehavior.Strict);
    readonly Mock<ITriagePipeline> _triage = new(MockBehavior.Strict);
    readonly Mock<IBranchResolver> _resolver = new(MockBehavior.Strict);
    readonly Mock<IDispatcher> _dispatcher = new(MockBehavior.Strict);
    readonly Mock<IStateStore> _store = new(MockBehavior.Strict);
    readonly Mock<IGhClient> _gh = new(MockBehavior.Strict);
    readonly Mock<IHostApplicationLifetime> _lifetime = new();
    readonly DaemonOptions _options = new()
    {
        AiUserLogin = "jon-or-ai",
        DataDir = Path.Combine(Path.GetTempPath(), "aidaemon-worker-tests"),
        WorktreeRoot = @"C:\Users\Jon\worktrees",
        RepoAllowlist = new() { "ownerrez/orez" },
    };

    Worker Build()
    {
        // Daily processed-prune gate: every TickAsync call reads the last-pruned kv and either
        // skips or runs PruneProcessedAsync. Default to "already pruned recently" so individual
        // tests don't have to wire it; the prune behavior itself has its own focused tests.
        _store.Setup(s => s.GetKvAsync(StateStoreKeys.ProcessedLastPruned, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return new Worker(
            NullLogger<Worker>.Instance,
            Options.Create(_options),
            _lifetime.Object,
            _store.Object,
            _poller.Object,
            _triage.Object,
            _resolver.Object,
            _dispatcher.Object,
            _gh.Object);
    }

    static GhNotification N(string id, string commentUrl, DateTimeOffset? updated = null) => new()
    {
        Id = id,
        Reason = "mention",
        Unread = true,
        UpdatedAt = updated ?? DateTimeOffset.UtcNow,
        Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        Subject = new GhNotificationSubject
        {
            Title = $"Thread {id}",
            Type = "Issue",
            Url = $"https://api.github.com/repos/o/r/issues/{id}",
            LatestCommentUrl = commentUrl,
        },
    };

    static BranchInfo SameBranch() => new(
        "ownerrez/orez", "16119-isdpvirtualproperty",
        @"D:\git\orez.worktrees\16119-isdpvirtualproperty",
        PrNumber: null, IssueNumber: 16119);

    void StubPoller(params GhNotification[] items)
    {
        _poller.Setup(p => p.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsync(items));
    }

    static async IAsyncEnumerable<GhNotification> ToAsync(GhNotification[] items)
    {
        foreach (var n in items)
        {
            await Task.Yield();
            yield return n;
        }
    }

    [Fact]
    public async Task Tick_GroupsTwoNotificationsForSameBranch_IntoOneAgentTriageAndOneDispatch()
    {
        // The central invariant the worker exists to enforce: N notifications resolving
        // to one branch produce exactly one agent classification and one dispatch, even as
        // each notification's MarkProcessed row is written individually.
        var n1 = N("thread-A", "https://api.github.com/repos/o/r/issues/comments/1");
        var n2 = N("thread-B", "https://api.github.com/repos/o/r/issues/comments/2");
        StubPoller(n1, n2);

        _triage.Setup(t => t.QuickTriageAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GhNotification _, CancellationToken _) => (null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SameBranch());

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

        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.IncrementRateLimitAsync(
                It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await Build().TickAsync(default);

        // Exactly one agent triage saw both items.
        _triage.Verify(t => t.AgentTriageAsync(
            It.Is<IReadOnlyList<NotificationWithBody>>(x => x.Count == 2),
            It.IsAny<BranchInfo>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.NotNull(capturedItems);
        Assert.Equal(2, capturedItems!.Count);

        // Exactly one dispatch.
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<BranchInfo>(),
            It.Is<IReadOnlyList<NotificationWithBody>>(x => x.Count == 2),
            It.IsAny<TriageVerdict>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Both notifications individually marked processed with spawned outcome.
        _store.Verify(s => s.MarkProcessedAsync(
            "thread-A", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("spawned:")),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(s => s.MarkProcessedAsync(
            "thread-B", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("spawned:")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Rate limit charged once per unique thread on dispatch — not per L1 entry.
        _store.Verify(s => s.IncrementRateLimitAsync("thread-A", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.IncrementRateLimitAsync("thread-B", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tick_QuickTriageThrows_MarksFailedAndContinues()
    {
        var n1 = N("thread-A", "https://api.github.com/repos/o/r/issues/comments/1");
        var n2 = N("thread-B", "https://api.github.com/repos/o/r/issues/comments/2");
        StubPoller(n1, n2);

        _triage.Setup(t => t.QuickTriageAsync(
                It.Is<GhNotification>(g => g.Id == "thread-A"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("gh hiccup"));
        _triage.Setup(t => t.QuickTriageAsync(
                It.Is<GhNotification>(g => g.Id == "thread-B"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)TriageVerdict.Drop("L1 unsupported"), "", "alice"));

        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Build().TickAsync(default);

        // The throwing notification MUST be marked processed — otherwise the cursor advances
        // past it and we lose the row forever.
        _store.Verify(s => s.MarkProcessedAsync(
            "thread-A", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("failed:quick-triage:")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // The other notification still gets handled normally.
        _store.Verify(s => s.MarkProcessedAsync(
            "thread-B", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("dropped:")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Resolver and downstream pieces never touched the throwing thread.
        _resolver.Verify(r => r.ResolveAsync(It.Is<GhNotification>(g => g.Id == "thread-A"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Tick_DispatchFailed_DoesNotChargeRateLimit()
    {
        // A failed dispatch should not consume the per-thread daily budget. Otherwise a
        // flaky claude-spawn run would silently eat the user's actionable allowance.
        var n = N("thread-A", "https://api.github.com/repos/o/r/issues/comments/1");
        StubPoller(n);

        _triage.Setup(t => t.QuickTriageAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SameBranch());
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
        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Build().TickAsync(default);

        _store.Verify(s => s.IncrementRateLimitAsync(
            It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(s => s.MarkProcessedAsync(
            "thread-A", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("failed:")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Tick_AgentTriageDrop_MarksAllItemsDropped_WithoutDispatch()
    {
        var n1 = N("thread-A", "https://api.github.com/repos/o/r/issues/comments/1");
        var n2 = N("thread-B", "https://api.github.com/repos/o/r/issues/comments/2");
        StubPoller(n1, n2);

        _triage.Setup(t => t.QuickTriageAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SameBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Drop("noise"));
        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Build().TickAsync(default);

        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<BranchInfo>(),
            It.IsAny<IReadOnlyList<NotificationWithBody>>(),
            It.IsAny<TriageVerdict>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(s => s.IncrementRateLimitAsync(
            It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);

        foreach (var id in new[] { "thread-A", "thread-B" })
            _store.Verify(s => s.MarkProcessedAsync(
                id, It.IsAny<string>(),
                It.Is<string>(o => o.StartsWith("dropped:")),
                It.IsAny<CancellationToken>()),
                Times.Once);
    }

    [Fact]
    public async Task Tick_ResolveCachedPerTick_AvoidsDuplicateNetworkAndGitWork()
    {
        // Two notifications, same subject URL → resolver should be called once per tick.
        // Real-world: a PR with five comments fires five notifications; each used to
        // trigger gh+git for the same PR. The per-tick cache avoids that.
        var n1 = N("thread-A", "https://api.github.com/repos/o/r/issues/comments/1");
        var n2 = N("thread-B", "https://api.github.com/repos/o/r/issues/comments/2");
        // Both notifications point at the same subject URL.
        n1.Subject.Url = "https://api.github.com/repos/o/r/pulls/42";
        n2.Subject.Url = "https://api.github.com/repos/o/r/pulls/42";
        StubPoller(n1, n2);

        _triage.Setup(t => t.QuickTriageAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SameBranch());
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
        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.IncrementRateLimitAsync(
                It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await Build().TickAsync(default);

        _resolver.Verify(r => r.ResolveAsync(
            It.IsAny<GhNotification>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Tick_EmitsMetrics_OneCounterPerOutcomeBucket()
    {
        // Two notifications: one drops at L1, one dispatches Spawned. The metrics surface
        // is contract for whatever exporter an operator wires up.
        var n1 = N("thread-DROP", "https://api.github.com/repos/o/r/issues/comments/1");
        var n2 = N("thread-GO", "https://api.github.com/repos/o/r/issues/comments/2");
        StubPoller(n1, n2);

        _triage.Setup(t => t.QuickTriageAsync(
                It.Is<GhNotification>(g => g.Id == "thread-DROP"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)TriageVerdict.Drop("noise"), "", "alice"));
        _triage.Setup(t => t.QuickTriageAsync(
                It.Is<GhNotification>(g => g.Id == "thread-GO"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SameBranch());
        _triage.Setup(t => t.AgentTriageAsync(
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<BranchInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageVerdict.Actionable("go", "x", 0.9));
        _dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<BranchInfo>(),
                It.IsAny<IReadOnlyList<NotificationWithBody>>(),
                It.IsAny<TriageVerdict>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchOutcome.Spawned);
        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.IncrementRateLimitAsync(
                It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "AiDaemon")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            observed.TryGetValue(instrument.Name, out var prev);
            observed[instrument.Name] = prev + value;
        });
        listener.Start();

        await Build().TickAsync(default);

        listener.Dispose();

        Assert.Equal(2, observed["aidaemon.tick.seen"]);
        Assert.Equal(1, observed["aidaemon.tick.dropped"]);
        Assert.Equal(1, observed["aidaemon.tick.actionable"]);
        Assert.Equal(0, observed.GetValueOrDefault("aidaemon.tick.failed"));
        // Both notifications resolved to the same branch; one of the two is the "primary" and
        // the second is the coalesced one.
        Assert.Equal(0, observed["aidaemon.tick.coalesced"]); // L1-drop wasn't grouped, so coalesced=0
    }

    [Fact]
    public async Task Tick_PruneGate_SkipsWhenLastPrunedIsRecent()
    {
        // Override the default "now" stub to a recent one and verify Prune isn't called.
        _store.Setup(s => s.GetKvAsync(StateStoreKeys.ProcessedLastPruned, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddHours(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _poller.Setup(p => p.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsync(Array.Empty<GhNotification>()));

        // Build manually so the default GetKv setup in Build() doesn't override ours.
        var w = new Worker(
            NullLogger<Worker>.Instance, Options.Create(_options), _lifetime.Object,
            _store.Object, _poller.Object, _triage.Object, _resolver.Object, _dispatcher.Object, _gh.Object);

        await w.TickAsync(default);

        _store.Verify(s => s.PruneProcessedAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Tick_PruneGate_RunsWhenLastPrunedIsStale()
    {
        // Last pruned > 24h ago → prune fires and the kv timestamp is updated.
        _store.Setup(s => s.GetKvAsync(StateStoreKeys.ProcessedLastPruned, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddDays(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _store.Setup(s => s.PruneProcessedAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        _store.Setup(s => s.SetKvAsync(StateStoreKeys.ProcessedLastPruned, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _poller.Setup(p => p.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsync(Array.Empty<GhNotification>()));

        var w = new Worker(
            NullLogger<Worker>.Instance, Options.Create(_options), _lifetime.Object,
            _store.Object, _poller.Object, _triage.Object, _resolver.Object, _dispatcher.Object, _gh.Object);

        await w.TickAsync(default);

        // Cutoff should be ~30 days ago; assert it's well in the past, not "now".
        _store.Verify(s => s.PruneProcessedAsync(
            It.Is<DateTimeOffset>(c => c < DateTimeOffset.UtcNow.AddDays(-29) && c > DateTimeOffset.UtcNow.AddDays(-31)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(s => s.SetKvAsync(
            StateStoreKeys.ProcessedLastPruned, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Tick_PruneFails_DoesNotAbortTick()
    {
        // A SQLite hiccup during prune must never block the actual tick work.
        _store.Setup(s => s.GetKvAsync(StateStoreKeys.ProcessedLastPruned, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null); // never pruned
        _store.Setup(s => s.PruneProcessedAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk full"));
        _poller.Setup(p => p.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsync(Array.Empty<GhNotification>()));

        var w = new Worker(
            NullLogger<Worker>.Instance, Options.Create(_options), _lifetime.Object,
            _store.Object, _poller.Object, _triage.Object, _resolver.Object, _dispatcher.Object, _gh.Object);

        // Should not throw — the catch in TryPruneProcessedAsync swallows non-OCE.
        await w.TickAsync(default);

        // The kv should NOT have been updated (the prune failed, so don't suppress next attempt).
        _store.Verify(s => s.SetKvAsync(
            StateStoreKeys.ProcessedLastPruned, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Tick_ResolverThrows_MarksFailedResolveAndSkips()
    {
        var n = N("thread-X", "https://api.github.com/repos/o/r/issues/comments/9");
        StubPoller(n);

        _triage.Setup(t => t.QuickTriageAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TriageVerdict?)null, "body", "alice"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<GhNotification>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git wedged"));
        _store.Setup(s => s.MarkProcessedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Build().TickAsync(default);

        _store.Verify(s => s.MarkProcessedAsync(
            "thread-X", It.IsAny<string>(),
            It.Is<string>(o => o.StartsWith("failed:resolve:")),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _triage.Verify(t => t.AgentTriageAsync(
            It.IsAny<IReadOnlyList<NotificationWithBody>>(),
            It.IsAny<BranchInfo>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

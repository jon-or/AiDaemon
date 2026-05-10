using System.Text.Json;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests.Services;

public class TriagePipelineTests : IDisposable
{
    readonly string _dbPath;
    readonly SqliteStateStore _store;
    readonly Mock<IGhClient> _gh = new();
    readonly Mock<IClaudeRunner> _claude = new();
    readonly DaemonOptions _options;
    readonly TriagePipeline _pipeline;

    public TriagePipelineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"triage-tests-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _store = new SqliteStateStore(connStr, NullLogger<SqliteStateStore>.Instance);
        _store.InitializeAsync(default).GetAwaiter().GetResult();

        _options = new DaemonOptions
        {
            AiUserLogin = "jon-or-ai",
            ActionableReasons = new() { "mention", "review_requested", "team_mention", "assign", "comment", "author" },
            BotAuthorBlocklist = new() { "dependabot[bot]", "renovate[bot]", "github-actions[bot]" },
            Triage = new TriageOptions
            {
                Model = "haiku",
                MaxActionsPerThreadPerDay = 5,
                L2DropPatterns = new() { @"^\s*(thanks|lgtm|approved|👍|:\+1:)\s*$" },
            },
        };

        _pipeline = new TriagePipeline(
            _gh.Object,
            _claude.Object,
            _store,
            Options.Create(_options),
            NullLogger<TriagePipeline>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }

    static GhNotification N(string type = "Issue", string reason = "mention",
        string? commentUrl = "https://api.github.com/repos/o/r/issues/comments/1") => new()
    {
        Id = "thread-1",
        Reason = reason,
        UpdatedAt = DateTimeOffset.UtcNow,
        Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        Subject = new GhNotificationSubject
        {
            Title = "Some thread",
            Type = type,
            Url = "https://api.github.com/repos/o/r/issues/1",
            LatestCommentUrl = commentUrl,
        },
    };

    static BranchInfo Branch() => new("ownerrez/orez", "16119-isdpvirtualproperty",
        @"D:\git\orez.worktrees\16119-isdpvirtualproperty", PrNumber: null, IssueNumber: 16119);

    void StubComment(string body, string author = "alice")
    {
        _gh.Setup(g => g.GetCommentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommentInfo
            {
                Id = 1,
                Body = body,
                User = new GhUserRef { Login = author, Type = "User" },
            });
    }

    void StubAgent(string action, double confidence, string why = "test")
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            action,
            confidence,
            why,
        });

        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ClaudeJsonResult(false, "", json, "end_turn", 100));
    }

    // ==================== QuickTriage (L1 + L2) ====================

    [Fact]
    public async Task Quick_DropsUnsupportedSubjectType()
    {
        var (v, body, _) = await _pipeline.QuickTriageAsync(N(type: "CheckSuite"), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("unsupported subject type", v.Why);
        Assert.Equal("", body);  // L1 short-circuit, no body fetched
    }

    [Fact]
    public async Task Quick_DropsReasonNotInActionableList()
    {
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(reason: "subscribed"), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("ActionableReasons", v.Why);
    }

    [Fact]
    public async Task Quick_ReturnsBodyAlongsideVerdict_OnBotDrop()
    {
        StubComment("any body", author: "dependabot[bot]");
        var (v, body, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Equal("any body", body);  // body fetched + returned even on bot-drop
    }

    [Fact]
    public async Task Quick_DropsSelfAuthored()
    {
        StubComment("any body", author: "jon-or-ai");
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("self-authored", v.Why);
    }

    [Fact]
    public async Task Quick_DropsBotAuthor()
    {
        StubComment("any body", author: "dependabot[bot]");
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("blocklisted bot author", v.Why);
    }

    [Fact]
    public async Task Quick_RateLimit_DropsWhenCounterAtOrAboveCap()
    {
        // The pipeline does NOT touch the rate-limit table — only the worker does on
        // successful dispatch. So pre-seed the table directly to simulate "this thread has
        // already received its budget today" and assert the next QuickTriage drops at L1.
        StubComment("Question?");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var i = 0; i < _options.Triage.MaxActionsPerThreadPerDay; i++)
            await _store.IncrementRateLimitAsync("thread-1", today, default);

        var (capped, body, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(capped);
        Assert.Equal(TriageAction.Drop, capped!.Action);
        Assert.Contains("rate limit", capped.Why, StringComparison.OrdinalIgnoreCase);
        // L1 short-circuits before fetching the comment — no body should be returned.
        Assert.Equal("", body);
        _gh.Verify(g => g.GetCommentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Quick_DroppedAtL2_DoesNotChargeRateLimit()
    {
        // The increment moved to the dispatch path, so L2-dropped notifications must not
        // bump the per-thread counter. Otherwise a thread of "lgtm" comments could starve
        // the day's budget without ever producing a dispatch.
        StubComment("lgtm");
        await _pipeline.QuickTriageAsync(N(), default);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(0, await _store.GetRateLimitAsync("thread-1", today, default));
    }

    [Fact]
    public async Task Quick_PassedToL3_DoesNotChargeRateLimit()
    {
        StubComment("substantive question that needs the agent");
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.Null(v);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(0, await _store.GetRateLimitAsync("thread-1", today, default));
    }

    [Theory]
    [InlineData("lgtm")]
    [InlineData("LGTM")]
    [InlineData("  thanks  ")]
    [InlineData("approved")]
    [InlineData("👍")]
    public async Task Quick_DropsNoiseRegex(string body)
    {
        StubComment(body);
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("L2 regex", v.Why);
    }

    [Fact]
    public async Task Quick_StripsQuotedRepliesBeforeMatching()
    {
        var body = "> earlier text\n> more quoted text\n\nlgtm";
        StubComment(body);
        var (v, _, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.NotNull(v);
        Assert.Equal(TriageAction.Drop, v!.Action);
        Assert.Contains("L2 regex", v.Why);
    }

    [Fact]
    public async Task Quick_ReturnsNullWithBody_WhenL1AndL2DontDecide()
    {
        // Real, substantive comment body that isn't noise — should bubble up to L3.
        StubComment("can you bump the timeout in foo.cs to 30s?");
        var (v, body, _) = await _pipeline.QuickTriageAsync(N(), default);
        Assert.Null(v);
        Assert.Equal("can you bump the timeout in foo.cs to 30s?", body);
    }

    [Fact]
    public void StripQuotedReplies_DropsLeadingQuotedBlock()
    {
        var input = "> quoted line 1\n> quoted line 2\n\nactual reply";
        Assert.Equal("actual reply", TriagePipeline.StripQuotedReplies(input));
    }

    [Fact]
    public void StripQuotedReplies_HandlesCRLF()
    {
        var input = "> quoted\r\n\r\nactual";
        Assert.Equal("actual", TriagePipeline.StripQuotedReplies(input));
    }

    // ==================== AgentTriage (L3) ====================

    [Fact]
    public async Task Agent_RunsClaudeInScratchDir_NoSessionId_NoTools()
    {
        StubAgent("actionable", 0.95);

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);

        // Triage runs in <DataDir>/triage-scratch (NOT the worktree), with no session-id
        // (--no-session-persistence) and no permission-mode (no tools — pure classifier).
        var expectedScratch = Path.Combine(_options.DataDir, "triage-scratch");
        _claude.Verify(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                expectedScratch,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                null,   // sessionId
                null),  // permissionMode
            Times.Once);
    }

    [Fact]
    public async Task Agent_DropVerdict_HonoredDirectly()
    {
        StubAgent("drop", 0.95, why: "status update");

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Equal("status update", v.Why);
    }

    [Fact]
    public async Task Agent_LowConfidenceDrop_StillHonoredAsDrop()
    {
        // No bias rule — the LLM's verdict is taken directly. Pin the verdict mapping
        // (action / confidence / why) so a regression in plumbing fails this test.
        StubAgent("drop", 0.6, why: "borderline");

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Equal(0.6, v.Confidence);
        Assert.Equal("borderline", v.Why);
        // Triage no longer produces a summary — pre-run owns that field now.
        Assert.Equal("", v.Summary);
    }

    [Fact]
    public async Task Agent_ActionableVerdict_HonoredDirectly()
    {
        StubAgent("actionable", 0.4, why: "needs review");

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Equal("needs review", v.Why);
    }

    [Fact]
    public async Task Agent_Throws_FallsBackToActionableWithSessionId()
    {
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("test"));

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Contains("agent error", v.Why);
    }

    [Fact]
    public async Task Agent_IsErrorTrue_FallsBackToActionable()
    {
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ClaudeJsonResult(true, "Not logged in", null, null, 50));

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
    }

    [Fact]
    public async Task Agent_BuildAgentInput_IncludesBranchAndAllBodiesInOrder()
    {
        // Pin BuildAgentInput's payload so a regression there (e.g. dropped bodies, swapped
        // ordering) fails this test rather than silently degrading the classifier.
        string? capturedUserInput = null;
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback((string _, string userInput, string _, string _, string _, TimeSpan _, CancellationToken _, string? _, string? _) =>
                capturedUserInput = userInput)
            .ReturnsAsync(new ClaudeJsonResult(false, "",
                JsonSerializer.SerializeToElement(new { action = "actionable", confidence = 0.9, why = "x" }),
                "end_turn", 100));

        var older = new GhNotification
        {
            Id = "thread-A",
            Reason = "mention",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
            Subject = new GhNotificationSubject
            {
                Type = "Issue", Title = "older issue title",
                Url = "https://api.github.com/repos/o/r/issues/1",
                LatestCommentUrl = "https://api.github.com/repos/o/r/issues/comments/1",
            },
        };
        var newer = new GhNotification
        {
            Id = "thread-B",
            Reason = "review_requested",
            UpdatedAt = DateTimeOffset.UtcNow,
            Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
            Subject = new GhNotificationSubject
            {
                Type = "PullRequest", Title = "newer pr title",
                Url = "https://api.github.com/repos/o/r/pulls/2",
                LatestCommentUrl = "https://api.github.com/repos/o/r/pulls/comments/2",
            },
        };

        var items = new[]
        {
            new NotificationWithBody(newer, "BODY-NEWER-MARKER"),
            new NotificationWithBody(older, "BODY-OLDER-MARKER"),
        };

        await _pipeline.AgentTriageAsync(items, Branch(), default);

        Assert.NotNull(capturedUserInput);
        // Branch metadata.
        Assert.Contains("16119-isdpvirtualproperty", capturedUserInput);
        Assert.Contains("ownerrez/orez", capturedUserInput);
        Assert.Contains("Issue: #16119", capturedUserInput);
        // Multi-item header.
        Assert.Contains("2 notifications", capturedUserInput);
        // Both bodies present.
        Assert.Contains("BODY-NEWER-MARKER", capturedUserInput);
        Assert.Contains("BODY-OLDER-MARKER", capturedUserInput);
        // Older comes first (chronological order in the user message).
        var iOlder = capturedUserInput.IndexOf("BODY-OLDER-MARKER", StringComparison.Ordinal);
        var iNewer = capturedUserInput.IndexOf("BODY-NEWER-MARKER", StringComparison.Ordinal);
        Assert.True(iOlder < iNewer, "older notification body should appear before newer in BuildAgentInput");
        // Per-notification headers.
        Assert.Contains("older issue title", capturedUserInput);
        Assert.Contains("newer pr title", capturedUserInput);
    }

    [Fact]
    public async Task Agent_NoStructuredOutput_FallsBackToActionable()
    {
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ClaudeJsonResult(false, "", null, "end_turn", 50));

        var v = await _pipeline.AgentTriageAsync(new[] { new NotificationWithBody(N(), "test body") }, Branch(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
    }
}

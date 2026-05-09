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

    void StubL3(string action, double confidence, string why = "test", string summary = "")
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            action,
            confidence,
            why,
            summary = string.IsNullOrEmpty(summary) ? "stubbed" : summary,
        });

        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClaudeJsonResult(false, "", json, "end_turn", 100));
    }

    // ---------- L1: author / type / reason ----------

    [Fact]
    public async Task L1_DropsUnsupportedSubjectType()
    {
        var v = await _pipeline.TriageAsync(N(type: "CheckSuite"), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("unsupported subject type", v.Why);
    }

    [Fact]
    public async Task L1_DropsReasonNotInActionableList()
    {
        var v = await _pipeline.TriageAsync(N(reason: "subscribed"), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("ActionableReasons", v.Why);
    }

    [Fact]
    public async Task L1_ReviewRequested_ShortcutsToActionableWithoutFetchingBody()
    {
        var v = await _pipeline.TriageAsync(N(type: "PullRequest", reason: "review_requested"), default);
        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Equal("review_requested", v.Why);
        _gh.Verify(g => g.GetCommentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _claude.Verify(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task L1_DropsSelfAuthored()
    {
        StubComment("any body", author: "jon-or-ai");
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("self-authored", v.Why);
    }

    [Fact]
    public async Task L1_DropsBotAuthor()
    {
        StubComment("any body", author: "dependabot[bot]");
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("blocklisted bot author", v.Why);
    }

    [Fact]
    public async Task L1_RateLimit_DropsAfterMaxPerDay()
    {
        StubComment("question?");
        StubL3("actionable", 0.9);

        for (var i = 0; i < _options.Triage.MaxActionsPerThreadPerDay; i++)
        {
            var ok = await _pipeline.TriageAsync(N(), default);
            Assert.Equal(TriageAction.Actionable, ok.Action);
        }

        var capped = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Drop, capped.Action);
        Assert.Contains("rate limit", capped.Why, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- L2: regex content filter ----------

    [Theory]
    [InlineData("lgtm")]
    [InlineData("LGTM")]
    [InlineData("  thanks  ")]
    [InlineData("approved")]
    [InlineData("👍")]
    public async Task L2_DropsNoiseRegex(string body)
    {
        StubComment(body);
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("L2 regex", v.Why);
    }

    [Fact]
    public async Task L2_StripsQuotedRepliesBeforeMatching()
    {
        // The body has a quoted-reply followed by the actual content "lgtm" — should still drop.
        var body = "> earlier text\n> more quoted text\n\nlgtm";
        StubComment(body);
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Drop, v.Action);
        Assert.Contains("L2 regex", v.Why);
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

    [Fact]
    public async Task L2_DoesNotMatchRealBodyWithNoiseSubstrings()
    {
        // "thanks" embedded in a longer body shouldn't match the noise regex.
        StubComment("thanks for the patch — but can you also handle the timeout case?");
        StubL3("actionable", 0.95);
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Actionable, v.Action);
    }

    // ---------- L3: LLM call + asymmetric bias ----------

    [Fact]
    public async Task L3_HighConfidenceDrop_NoQuestion_NoMention_HonoredAsDrop()
    {
        StubComment("Just FYI, deploy went through last night.");
        StubL3("drop", 0.95, why: "status update");

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Drop, v.Action);
    }

    [Fact]
    public async Task L3_LowConfidenceDrop_UpgradedToActionable()
    {
        StubComment("Looks fine to me.");
        StubL3("drop", 0.6, why: "borderline");

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Contains("low-confidence", v.Why);
    }

    [Fact]
    public async Task L3_DropWithQuestionMark_UpgradedToActionable()
    {
        StubComment("Is the cutover scheduled for tomorrow?");
        StubL3("drop", 0.95, why: "rhetorical");

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Contains("has-question", v.Why);
    }

    [Fact]
    public async Task L3_DropWithAtMention_UpgradedToActionable()
    {
        StubComment("Hey @jon-or-ai, fyi this looks correct.");
        StubL3("drop", 0.95, why: "fyi-only");

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Contains("at-mention", v.Why);
    }

    [Fact]
    public async Task L3_ActionableNotDemoted_RegardlessOfConfidence()
    {
        StubComment("Could you bump the timeout?");
        StubL3("actionable", 0.4, why: "low-conf actionable still actionable");

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.DoesNotContain("upgraded", v.Why);
    }

    [Fact]
    public async Task L3_Throws_FallsBackToActionable()
    {
        StubComment("non-trivial body");
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("test"));

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
        Assert.Contains("L3 error", v.Why);
    }

    [Fact]
    public async Task L3_IsErrorTrue_FallsBackToActionable()
    {
        StubComment("non-trivial body");
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClaudeJsonResult(true, "Not logged in", null, null, 50));

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
    }

    [Fact]
    public async Task L3_NoStructuredOutput_FallsBackToActionable()
    {
        StubComment("non-trivial body");
        _claude.Setup(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClaudeJsonResult(false, "", null, "end_turn", 50));

        var v = await _pipeline.TriageAsync(N(), default);

        Assert.Equal(TriageAction.Actionable, v.Action);
    }

    [Fact]
    public async Task L3_EmptyBody_DefaultsToActionableWithoutCallingClaude()
    {
        StubComment(""); // empty body
        var v = await _pipeline.TriageAsync(N(), default);
        Assert.Equal(TriageAction.Actionable, v.Action);
        _claude.Verify(c => c.RunHeadlessJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

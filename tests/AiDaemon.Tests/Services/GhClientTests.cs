using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Process;
using AiDaemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests.Services;

public class GhClientTests
{
    readonly Mock<IProcessRunner> _runner = new(MockBehavior.Strict);
    readonly DaemonOptions _options = new() { GhPath = "gh" };

    GhClient Build() => new(_runner.Object, Options.Create(_options), NullLogger<GhClient>.Instance);

    void StubGh(string stdout, int exitCode = 0, string stderr = "")
    {
        _runner.Setup(r => r.RunAsync(
                "gh",
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(exitCode, stdout, stderr));
    }

    void CaptureGhArgs(out List<IReadOnlyList<string>> calls, string stdout = "[]", int exit = 0, string stderr = "")
    {
        var captured = new List<IReadOnlyList<string>>();
        _runner.Setup(r => r.RunAsync(
                "gh",
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<string> args, string? _, IReadOnlyDictionary<string, string?>? _, string? _, CancellationToken _) =>
                captured.Add(args.ToList()))
            .ReturnsAsync(new ProcessResult(exit, stdout, stderr));
        calls = captured;
    }

    // ---------- auth failure detection ----------

    [Theory]
    [InlineData("HTTP 401 Unauthorized")]
    [InlineData("HTTP 403 Forbidden: rate-limit-y stuff")]
    [InlineData("Bad credentials")]
    [InlineData("Run `gh auth login` to authenticate")]
    [InlineData("authentication is required")]
    public async Task RunGh_AuthLikeStderr_ThrowsGhAuthException(string stderr)
    {
        StubGh("", exitCode: 4, stderr: stderr);
        await Assert.ThrowsAsync<GhAuthException>(
            () => Build().ApiAsync<JsonElementBag>("/user", default));
    }

    [Fact]
    public async Task RunGh_GhTokenStderr_IsCaseSensitiveAndStillTriggersAuth()
    {
        // GH_TOKEN is the env-var name and must match exactly to be a true auth signal.
        StubGh("", exitCode: 4, stderr: "Set GH_TOKEN to authenticate non-interactively.");
        await Assert.ThrowsAsync<GhAuthException>(
            () => Build().ApiAsync<JsonElementBag>("/user", default));
    }

    [Fact]
    public async Task RunGh_NonAuthError_ThrowsGhCliExceptionNotAuth()
    {
        StubGh("", exitCode: 1, stderr: "gh: HTTP 500: Internal Server Error");
        var ex = await Assert.ThrowsAsync<GhCliException>(
            () => Build().ApiAsync<JsonElementBag>("/foo", default));
        Assert.IsNotType<GhAuthException>(ex);
        Assert.Equal(1, ex.ExitCode);
    }

    // ---------- 404-on-comment is null, not throw ----------

    [Fact]
    public async Task GetCommentAsync_404_ReturnsNullInsteadOfThrowing()
    {
        // Real gh stderr shape on a deleted comment.
        StubGh("", exitCode: 1, stderr: "gh: HTTP 404: Not Found (https://api.github.com/...)");
        var got = await Build().GetCommentAsync(
            "https://api.github.com/repos/o/r/issues/comments/999", default);
        Assert.Null(got);
    }

    [Fact]
    public async Task GetCommentAsync_NullOrWhitespaceUrl_ReturnsNull_NoGhCall()
    {
        // Don't stub the runner — strict mock will fail if gh is invoked.
        var got = await Build().GetCommentAsync("", default);
        Assert.Null(got);
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetCommentAsync_AbsoluteUrl_StripsHostBeforeGhCall()
    {
        CaptureGhArgs(out var calls,
            stdout: """{ "id": 1, "body": "hi", "user": { "login": "alice", "type": "User" } }""");
        await Build().GetCommentAsync(
            "https://api.github.com/repos/o/r/issues/comments/1", default);

        // gh api accepts both absolute URLs and bare paths; we strip the host so the call
        // works against alternate hosts (GHES) without special-casing.
        Assert.Single(calls);
        Assert.Contains("/repos/o/r/issues/comments/1", calls[0]);
        Assert.DoesNotContain(calls[0], a => a.Contains("api.github.com"));
    }

    // ---------- ListNotificationsAsync URL shape ----------

    [Fact]
    public async Task ListNotificationsAsync_BuildsSinceParameter_AsUtcIsoZ()
    {
        CaptureGhArgs(out var calls, stdout: "[]");
        var since = new DateTimeOffset(2026, 5, 9, 14, 30, 0, TimeSpan.FromHours(-5));
        await Build().ListNotificationsAsync(since, default);

        Assert.Single(calls);
        // Three positional args: api, --paginate, /notifications?...
        Assert.Equal("api", calls[0][0]);
        Assert.Equal("--paginate", calls[0][1]);
        Assert.StartsWith("/notifications?", calls[0][2]);
        Assert.Contains("participating=true", calls[0][2]);
        Assert.Contains("all=true", calls[0][2]);
        // since= is UTC-converted and ISO-Z formatted.
        Assert.Contains("since=2026-05-09T19%3A30%3A00Z", calls[0][2]);
    }

    [Fact]
    public async Task ListNotificationsAsync_NoSince_OmitsSinceParam()
    {
        CaptureGhArgs(out var calls, stdout: "[]");
        await Build().ListNotificationsAsync(null, default);
        Assert.DoesNotContain("since=", calls[0][2]);
    }

    // ---------- FindOpenPrNumberForBranchAsync ----------

    [Fact]
    public async Task FindOpenPrForBranch_ReturnsNumber_WhenExactlyOneOpenPr()
    {
        CaptureGhArgs(out var calls,
            stdout: """[{"number":16742,"head":{"ref":"16119-isdpvirtualproperty","sha":"x"},"base":{"ref":"master","sha":"y"}}]""");

        var got = await Build().FindOpenPrNumberForBranchAsync(
            "ownerrez/orez", "16119-isdpvirtualproperty", default);

        Assert.Equal(16742, got);
        Assert.Single(calls);
        // Path shape: /repos/<owner>/<repo>/pulls?state=open&head=<owner>:<branch>
        Assert.StartsWith("/repos/ownerrez/orez/pulls?", calls[0][1]);
        Assert.Contains("state=open", calls[0][1]);
        Assert.Contains("head=ownerrez%3A16119-isdpvirtualproperty", calls[0][1]);
    }

    [Fact]
    public async Task FindOpenPrForBranch_ReturnsNull_WhenZeroOpenPrs()
    {
        StubGh("[]");
        var got = await Build().FindOpenPrNumberForBranchAsync(
            "ownerrez/orez", "16119-isdpvirtualproperty", default);
        Assert.Null(got);
    }

    [Fact]
    public async Task FindOpenPrForBranch_ReturnsNull_WhenMultipleOpenPrs()
    {
        // Rare but possible (conflicting reopens, broken state). Don't guess — drop the
        // Open PR button rather than pick wrong.
        StubGh("""[{"number":16742,"head":{"ref":"16119-isdpvirtualproperty","sha":"x"},"base":{"ref":"master","sha":"y"}},{"number":16800,"head":{"ref":"16119-isdpvirtualproperty","sha":"z"},"base":{"ref":"master","sha":"y"}}]""");

        var got = await Build().FindOpenPrNumberForBranchAsync(
            "ownerrez/orez", "16119-isdpvirtualproperty", default);
        Assert.Null(got);
    }

    [Theory]
    [InlineData("", "16119-foo")]
    [InlineData("ownerrez/orez", "")]
    [InlineData("malformed-no-slash", "16119-foo")]
    [InlineData("trailing-slash/", "16119-foo")]
    public async Task FindOpenPrForBranch_BadInputs_ReturnNull_NoGhCall(string repo, string branch)
    {
        // Strict-mode runner means an unstubbed gh call would throw.
        var got = await Build().FindOpenPrNumberForBranchAsync(repo, branch, default);
        Assert.Null(got);
        _runner.VerifyNoOtherCalls();
    }

    // ---------- WhoAmIAsync ----------

    [Fact]
    public async Task WhoAmIAsync_ReadsLoginField()
    {
        StubGh("""{ "login": "jon-or-ai", "id": 123 }""");
        var got = await Build().WhoAmIAsync(default);
        Assert.Equal("jon-or-ai", got);
    }

    [Fact]
    public async Task WhoAmIAsync_NoLoginField_ReturnsEmptyString()
    {
        StubGh("""{ "id": 123 }""");
        var got = await Build().WhoAmIAsync(default);
        Assert.Equal("", got);
    }

    // ---------- malformed gh stdout ----------

    [Fact]
    public async Task ApiAsync_MalformedJson_ThrowsInvalidOperationWithFirst200Chars()
    {
        StubGh("<html><body>504 Gateway Time-out</body></html>");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().ApiAsync<JsonElementBag>("/user", default));
        // The wrapped JsonException is preserved as InnerException for diagnostics
        // (JsonReaderException : JsonException — accept the base type for forward-compat).
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Contains("First 200 chars", ex.Message);
    }

    // Local stand-in for a typed deserialization target (avoids dragging Models into
    // the test for a pure error-path assertion).
    class JsonElementBag
    {
        public string? Login { get; set; }
    }
}

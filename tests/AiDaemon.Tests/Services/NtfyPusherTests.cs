using System.Net;
using System.Text.Json;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDaemon.Tests.Services;

/// <summary>
/// Pins the wire shape NtfyPusher posts. We don't go to the network — a captive
/// HttpMessageHandler records the request URL + JSON body so the assertions can pin
/// exactly what the iOS / Android ntfy app will see.
/// </summary>
public class NtfyPusherTests
{
    readonly CapturingHandler _handler = new();
    readonly DaemonOptions _options = new()
    {
        Ntfy = new NtfyOptions
        {
            Server = "https://ntfy.example.com",
            Topic = "secret-topic-uuid",
            PriorityHigh = 4,
            PriorityNormal = 3,
        },
    };

    NtfyPusher Build()
    {
        var http = new HttpClient(_handler);
        return new NtfyPusher(http, Options.Create(_options), NullLogger<NtfyPusher>.Instance);
    }

    static BranchInfo Branch(
        string repo = "ownerrez/orez",
        string branch = "16119-isdpvirtualproperty",
        int? prNumber = null,
        int? issueNumber = 16119)
        => new(repo, branch, @"D:\git\orez.worktrees\16119-isdpvirtualproperty", prNumber, issueNumber);

    [Fact]
    public async Task PushSessionLink_PostsToServerRoot_WithJsonContentType()
    {
        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), "Test subject",
            TriageVerdict.Actionable("user asked a question"), default);

        Assert.Single(_handler.Calls);
        var (req, _) = _handler.Calls[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://ntfy.example.com/", req.RequestUri!.ToString());
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task PushSessionLink_TitleIsSubject_BodyHasMarkdownBranchAndSummary_HighPriority_RobotTag()
    {
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(issueNumber: 16119),
            "DP multiplier wrong on virtual properties",
            TriageVerdict.Actionable("question on virtual properties", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);

        Assert.Equal("secret-topic-uuid", json.GetProperty("topic").GetString());
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        // Title: the GitHub issue/PR title, not the branch.
        Assert.Equal("DP multiplier wrong on virtual properties", json.GetProperty("title").GetString());

        // markdown=true so ntfy renders the body's code spans / italics on the phone.
        Assert.True(json.GetProperty("markdown").GetBoolean());

        // Body: branch as inline code (renders smaller / monospace on the phone), then
        // summary. No Session line when the URL is real — Open Claude button covers it.
        var body = json.GetProperty("message").GetString()!;
        Assert.Equal(
            "`ownerrez/orez:16119-isdpvirtualproperty`\n\nInvestigate DP fix",
            body);

        var tags = json.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "robot" }, tags);
    }

    [Fact]
    public async Task PushSessionLink_NoSubjectTitle_FallsBackToBranchName()
    {
        // Defensive: empty subject title (notification with no subject — rare) → push
        // title falls back to the branch slug so the user still sees something.
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(), "",
            TriageVerdict.Actionable("ok"), default);

        var title = ParseBody(_handler.Calls[0].Body).GetProperty("title").GetString();
        Assert.Equal("16119-isdpvirtualproperty", title);
    }

    [Fact]
    public async Task PushSessionLink_BranchHasIssueOnly_RendersOpenIssue_AndOpenClaude()
    {
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(prNumber: null, issueNumber: 16119), "Test subject",
            TriageVerdict.Actionable("ok", summary: "Investigate"), default);

        var json = ParseBody(_handler.Calls[0].Body);
        var actions = json.GetProperty("actions").EnumerateArray().ToList();

        Assert.Equal(2, actions.Count);
        Assert.Equal("Open Issue", actions[0].GetProperty("label").GetString());
        Assert.Equal("https://github.com/ownerrez/orez/issues/16119", actions[0].GetProperty("url").GetString());
        Assert.Equal("Open Claude", actions[1].GetProperty("label").GetString());
        Assert.Equal("https://claude.ai/code/session_01ABC", actions[1].GetProperty("url").GetString());

        // No click target — we deliberately omit it so the ntfy app doesn't render the
        // auto COPY LINK / OPEN LINK buttons alongside our custom actions.
        Assert.False(json.TryGetProperty("click", out var click) && click.ValueKind != JsonValueKind.Null,
            "click should be absent so ntfy doesn't auto-render extra buttons");
    }

    [Fact]
    public async Task PushSessionLink_BranchHasPrOnly_RendersOpenPr_AndOpenClaude()
    {
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(prNumber: 16742, issueNumber: null), "Test subject",
            TriageVerdict.Actionable("ok", summary: "Address review"), default);

        var json = ParseBody(_handler.Calls[0].Body);
        var actions = json.GetProperty("actions").EnumerateArray().ToList();

        Assert.Equal(2, actions.Count);
        Assert.Equal("Open PR", actions[0].GetProperty("label").GetString());
        Assert.Equal("https://github.com/ownerrez/orez/pull/16742", actions[0].GetProperty("url").GetString());
        Assert.Equal("Open Claude", actions[1].GetProperty("label").GetString());
    }

    [Fact]
    public async Task PushSessionLink_BranchHasBothPrAndIssue_RendersAllThreeButtons()
    {
        // Cross-linked PR-and-issue branches surface both buttons; ntfy supports up to 3
        // custom view actions per notification — exactly enough for our layout.
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(prNumber: 16742, issueNumber: 16119), "Test subject",
            TriageVerdict.Actionable("ok", summary: "Both"), default);

        var json = ParseBody(_handler.Calls[0].Body);
        var actions = json.GetProperty("actions").EnumerateArray().ToList();

        Assert.Equal(3, actions.Count);
        Assert.Equal("Open PR", actions[0].GetProperty("label").GetString());
        Assert.Equal("Open Issue", actions[1].GetProperty("label").GetString());
        Assert.Equal("Open Claude", actions[2].GetProperty("label").GetString());

        Assert.Equal("https://github.com/ownerrez/orez/pull/16742",
            actions[0].GetProperty("url").GetString());
        Assert.Equal("https://github.com/ownerrez/orez/issues/16119",
            actions[1].GetProperty("url").GetString());
        Assert.Equal("https://claude.ai/code/session_01ABC",
            actions[2].GetProperty("url").GetString());
    }

    [Fact]
    public async Task PushSessionLink_NoPrNoIssue_StillRendersOpenClaude()
    {
        // Branch resolved with neither PR nor issue (rare — orphan branch). At minimum the
        // Open Claude button always renders so the user has a tap target.
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(prNumber: null, issueNumber: null), "Test subject",
            TriageVerdict.Actionable("ok"), default);

        var json = ParseBody(_handler.Calls[0].Body);
        var actions = json.GetProperty("actions").EnumerateArray().ToList();

        Assert.Single(actions);
        Assert.Equal("Open Claude", actions[0].GetProperty("label").GetString());
        Assert.Equal("https://claude.ai/code/session_01ABC", actions[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task PushHeadsUp_UsesNormalPriority_SameTitleAndButtonShape()
    {
        // No prefix on title — priority alone distinguishes session-link from heads-up on
        // the phone (PriorityHigh pings, PriorityNormal is silent).
        await Build().PushHeadsUpAsync(
            "https://claude.ai/code/session_01ABC", Branch(prNumber: 16742, issueNumber: 16119),
            "DP multiplier wrong on virtual properties",
            TriageVerdict.Actionable("followup", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);
        Assert.Equal(3, json.GetProperty("priority").GetInt32());
        Assert.Equal("DP multiplier wrong on virtual properties", json.GetProperty("title").GetString());

        // Same 3-button layout as session-link — heads-up only differs on priority.
        var actions = json.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(3, actions.Count);
    }

    [Fact]
    public async Task PushSessionLink_NotAvailableUrl_OpenClaudeFallsBackToClaudeHome_BodyShowsNotAvailable()
    {
        // Dispatcher passes "Not Available" when RC spawn fails. Open Claude button still
        // renders (better than no button at all) but points at claude.ai/code as a fallback.
        // Body's Session line shows "Not Available" so the user knows before tapping.
        await Build().PushSessionLinkAsync(
            "Not Available", Branch(prNumber: null, issueNumber: 16119), "Test subject",
            TriageVerdict.Actionable("question", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);

        var body = json.GetProperty("message").GetString()!;
        // Italic styling on the "Not Available" line so it reads as a sub-status note.
        Assert.Contains("_Session Not Available_", body);

        var actions = json.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(2, actions.Count);
        Assert.Equal("Open Issue", actions[0].GetProperty("label").GetString());
        Assert.Equal("Open Claude", actions[1].GetProperty("label").GetString());
        Assert.Equal("https://claude.ai/code", actions[1].GetProperty("url").GetString());
    }

    [Fact]
    public async Task PushSessionLink_VerdictWithoutSummary_BodyIsJustMarkdownBranch()
    {
        // L1/L2 verdicts (e.g. "review_requested" shortcut) have no summary; body should
        // collapse to just the markdown branch line — no Session line either, since the
        // Open Claude button covers the URL.
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(), "Test subject",
            TriageVerdict.Actionable("review_requested"), default);

        var body = ParseBody(_handler.Calls[0].Body).GetProperty("message").GetString()!;
        Assert.Equal("`ownerrez/orez:16119-isdpvirtualproperty`", body);
    }

    [Fact]
    public async Task EmptyTopic_SkipsPostAndDoesNotThrow()
    {
        // Forgotten / unset Ntfy.Topic shouldn't crash dispatch — push is best-effort.
        _options.Ntfy.Topic = "";

        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), "Test subject", TriageVerdict.Actionable("anything"), default);

        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task Non2xxResponse_IsLogged_NotThrown()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid topic"),
        };

        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), "Test subject", TriageVerdict.Actionable("anything"), default);

        Assert.Single(_handler.Calls);
    }

    [Fact]
    public async Task NetworkException_IsSwallowed_NotThrown()
    {
        // Phone tethered, ntfy.sh unreachable — a successful dispatch shouldn't roll back
        // because the push failed.
        _handler.Throw = new HttpRequestException("connection refused");

        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), "Test subject", TriageVerdict.Actionable("anything"), default);

        // No assertion failure means no exception escaped.
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        // Shutdown path: pusher should respect the stopping token, not swallow it.
        _handler.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build().PushSessionLinkAsync(
                "http://172.16.5.10:1234", Branch(), "Test subject", TriageVerdict.Actionable("anything"), default));
    }

    [Fact]
    public async Task UnicodeInSummary_GoesThroughUtf8Json_NotMojibake()
    {
        // Header-form ntfy would need RFC 2047 encoding; JSON form preserves UTF-8 directly.
        // Pin that we picked the JSON path so emoji + smart quotes round-trip.
        await Build().PushSessionLinkAsync(
            "https://claude.ai/code/session_01ABC", Branch(), "Test subject",
            TriageVerdict.Actionable("ok", summary: "Thanks 🎉 for the “fix”"),
            default);

        var msg = ParseBody(_handler.Calls[0].Body).GetProperty("message").GetString();
        Assert.Contains("🎉", msg);
        Assert.Contains("“fix”", msg);
    }

    static JsonElement ParseBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// HttpMessageHandler that records every outgoing request + body and returns a canned
    /// response (or throws). Lets us pin the exact wire shape without going to the network.
    /// </summary>
    class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));

            if (Throw != null)
                throw Throw;

            return Response;
        }
    }
}

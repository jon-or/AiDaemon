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

    static BranchInfo Branch(string repo = "ownerrez/orez", string branch = "16119-isdpvirtualproperty")
        => new(repo, branch, @"D:\git\orez.worktrees\16119-isdpvirtualproperty", PrNumber: null, IssueNumber: 16119);

    static GhNotification Notification(string title = "Bug in DP multiplier")
        => new()
        {
            Id = "23420840455",
            Reason = "mention",
            Subject = new GhNotificationSubject { Title = title, Type = "Issue", Url = "https://api.github.com/repos/ownerrez/orez/issues/16119" },
            Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
        };

    [Fact]
    public async Task PushSessionLink_PostsToServerRoot_WithJsonContentType()
    {
        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), Notification(),
            TriageVerdict.Actionable("user asked a question"), default);

        Assert.Single(_handler.Calls);
        var (req, _) = _handler.Calls[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://ntfy.example.com/", req.RequestUri!.ToString());
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task PushSessionLink_BodyContainsTopic_Title_Priority_TagsRobot_ClickAndAction()
    {
        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), Notification("DP multiplier wrong on virtual"),
            TriageVerdict.Actionable("question on virtual properties", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);

        Assert.Equal("secret-topic-uuid", json.GetProperty("topic").GetString());
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        Assert.Equal("[ownerrez/orez:16119-isdpvirtualproperty] Investigate DP fix",
            json.GetProperty("title").GetString());
        // Body leads with the GitHub subject; verdict.Why follows after a blank line.
        Assert.Contains("DP multiplier wrong on virtual", json.GetProperty("message").GetString());
        Assert.Contains("question on virtual properties", json.GetProperty("message").GetString());

        var tags = json.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "robot" }, tags);

        Assert.Equal("http://172.16.5.10:1234", json.GetProperty("click").GetString());

        var actions = json.GetProperty("actions").EnumerateArray().ToList();
        Assert.Single(actions);
        Assert.Equal("view", actions[0].GetProperty("action").GetString());
        Assert.Equal("Open session", actions[0].GetProperty("label").GetString());
        Assert.Equal("http://172.16.5.10:1234", actions[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task PushHeadsUp_UsesUpdatePrefix_AndNormalPriority()
    {
        await Build().PushHeadsUpAsync(
            "http://172.16.5.10:1234", Branch(), Notification("DP multiplier wrong on virtual"),
            TriageVerdict.Actionable("followup", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);
        Assert.Equal(3, json.GetProperty("priority").GetInt32());
        Assert.StartsWith("[update] [ownerrez/orez:16119-isdpvirtualproperty]",
            json.GetProperty("title").GetString());
        Assert.Equal("Resume session",
            json.GetProperty("actions")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task PushSessionLink_NotAvailableUrl_OmitsClickAndActions_ShowsStatusInBody()
    {
        // The dispatcher passes "Not Available" as the URL when the RC relay is down. We
        // still want the push to fire (so the user sees an actionable thing arrived), just
        // without a click/action that would open nowhere.
        await Build().PushSessionLinkAsync(
            "Not Available", Branch(), Notification("Bug in DP multiplier"),
            TriageVerdict.Actionable("question on virtual properties", summary: "Investigate DP fix"),
            default);

        var json = ParseBody(_handler.Calls[0].Body);

        // Same priority and title as a normal session-link push — there's no separate prefix.
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        Assert.StartsWith("[ownerrez/orez:16119-isdpvirtualproperty]",
            json.GetProperty("title").GetString());

        Assert.False(json.TryGetProperty("click", out var click) && click.ValueKind != JsonValueKind.Null,
            "click should be absent when URL is the literal \"Not Available\"");
        Assert.False(json.TryGetProperty("actions", out var actions) && actions.ValueKind != JsonValueKind.Null,
            "actions should be absent when URL is the literal \"Not Available\"");

        // Body shows the status so the user knows why there's no tappable button.
        var body = json.GetProperty("message").GetString()!;
        Assert.Contains("Session: Not Available", body);
        // Subject + verdict.Why still there for context.
        Assert.Contains("Bug in DP multiplier", body);
        Assert.Contains("question on virtual properties", body);
    }

    [Fact]
    public async Task EmptyTopic_SkipsPostAndDoesNotThrow()
    {
        // Forgotten / unset Ntfy.Topic shouldn't crash dispatch — push is best-effort.
        _options.Ntfy.Topic = "";

        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), Notification(),
            TriageVerdict.Actionable("anything"), default);

        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task EmptyClickUrl_OmitsClickAndActions()
    {
        // RC server down (the Phase-5 smoke-test scenario) — push should still go out so the
        // user sees there's a notification, just without a tappable URL.
        await Build().PushSessionLinkAsync(
            "", Branch(), Notification("RC down today"),
            TriageVerdict.Actionable("hand-driven"), default);

        var json = ParseBody(_handler.Calls[0].Body);
        Assert.False(json.TryGetProperty("click", out var click) && click.ValueKind != JsonValueKind.Null,
            "click should be absent or null when no RC URL is available");
        Assert.False(json.TryGetProperty("actions", out var actions) && actions.ValueKind != JsonValueKind.Null,
            "actions should be absent or null when no RC URL is available");
    }

    [Fact]
    public async Task Non2xxResponse_IsLogged_NotThrown()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid topic"),
        };

        // Should not throw — push is best-effort.
        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), Notification(),
            TriageVerdict.Actionable("anything"), default);

        Assert.Single(_handler.Calls);
    }

    [Fact]
    public async Task NetworkException_IsSwallowed_NotThrown()
    {
        // Phone tethered, ntfy.sh unreachable — a successful dispatch shouldn't roll back
        // because the push failed.
        _handler.Throw = new HttpRequestException("connection refused");

        await Build().PushSessionLinkAsync(
            "http://172.16.5.10:1234", Branch(), Notification(),
            TriageVerdict.Actionable("anything"), default);

        // No assertion failure means no exception escaped.
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        // Shutdown path: pusher should respect the stopping token, not swallow it.
        _handler.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build().PushSessionLinkAsync(
                "http://172.16.5.10:1234", Branch(), Notification(),
                TriageVerdict.Actionable("anything"), default));
    }

    [Fact]
    public async Task Title_TruncatesAt120Chars_PreservesPrefix()
    {
        // Real branch slugs can run long; a 200-char summary shouldn't push the [repo:branch]
        // prefix off the visible title row on iOS.
        var longSummary = new string('x', 200);
        await Build().PushSessionLinkAsync(
            "http://x", Branch(), Notification(),
            TriageVerdict.Actionable("why", summary: longSummary), default);

        var title = ParseBody(_handler.Calls[0].Body).GetProperty("title").GetString()!;
        Assert.True(title.Length <= 121, $"expected truncated title, got {title.Length} chars");
        Assert.StartsWith("[ownerrez/orez:16119-isdpvirtualproperty]", title);
    }

    [Fact]
    public async Task UnicodeInTitle_GoesThroughUtf8Json_NotMojibake()
    {
        // Header-form ntfy would need RFC 2047 encoding; JSON form preserves UTF-8 directly.
        // Pin that we picked the JSON path so emoji + smart quotes round-trip.
        await Build().PushSessionLinkAsync(
            "http://x", Branch(), Notification("Thanks 🎉 for the “fix”"),
            TriageVerdict.Actionable("ok"), default);

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

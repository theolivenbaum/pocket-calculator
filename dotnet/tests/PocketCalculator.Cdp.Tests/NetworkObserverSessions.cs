using System.Text.Json.Nodes;
using System.Threading.Channels;
using PocketCalculator.Browser;
using PocketCalculator.Cdp;
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Ports of upstream b8586de's page.rs tests and a test of 34346af: a target's Network events
/// reach every session attached to it, while lifecycle events and Fetch pauses stay with the
/// driving session.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class NetworkObserverSessions
{
    private static (CdpContext Ctx, string Page, List<NetworkEvent> Events) Fixture()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        string unrelated = ctx.CreatePage();
        foreach ((string session, string target) in new[] { ("driver", page), ("observer-a", page), ("observer-b", page), ("unrelated", unrelated) })
        {
            ctx.Sessions[session] = target;
        }
        List<NetworkEvent> events = [.. new[]
        {
            ("document-internal", "Document", "https://example.test/", 200),
            ("script-1", "Script", "https://example.test/app.js", 404),
        }.Select(e => new NetworkEvent
        {
            RequestId = e.Item1,
            Url = e.Item3,
            Method = "GET",
            ResourceType = e.Item2,
            Status = e.Item4,
            Headers = new Dictionary<string, string>(),
            ResponseHeaders = new Dictionary<string, string>(),
            BodySize = 12,
            Timestamp = 42.0,
        })];
        ctx.PendingEvents.Clear();
        return (ctx, page, events);
    }

    private static List<string> Network(CdpContext ctx, string session) =>
        [.. ctx.PendingEvents
            .Where(e => e.SessionId == session && e.Method.StartsWith("Network.", StringComparison.Ordinal))
            .Select(e => e.Method + " " + e.Params?.ToJsonString())];

    private static void AssertObserverNetwork(CdpContext ctx)
    {
        List<string> driver = Network(ctx, "driver");
        Assert.NotEmpty(driver);
        Assert.Equal(driver, Network(ctx, "observer-a"));
        Assert.Equal(driver, Network(ctx, "observer-b"));
        Assert.Empty(Network(ctx, "unrelated"));
        Assert.All(
            ctx.PendingEvents.Where(e => e.SessionId?.StartsWith("observer", StringComparison.Ordinal) == true),
            e => Assert.StartsWith("Network.", e.Method, StringComparison.Ordinal));
    }

    [Fact]
    public void Navigation_network_events_reach_all_target_observers_with_identical_ids()
    {
        (CdpContext ctx, string page, List<NetworkEvent> events) = Fixture();
        Domains.Page.EmitNavigationEvents(
            ctx, "driver", "frame-1", "loader-1", "https://example.test/", page, events, WaitUntil.Load, false);
        AssertObserverNetwork(ctx);
    }

    [Fact]
    public void Live_network_events_reach_observers_once_without_replaying_old_events()
    {
        (CdpContext ctx, string page, List<NetworkEvent> events) = Fixture();
        Domains.Page.EmitRuntimeNetworkEvents(ctx, "driver", "frame-1", "https://example.test/", page, events);
        AssertObserverNetwork(ctx);
        // Events already queued are not copied again with the next batch.
        int start = ctx.PendingEvents.Count;
        Domains.Page.EmitRuntimeNetworkEvents(ctx, "driver", "frame-1", "https://example.test/", page, events[1..]);
        Assert.All(ctx.PendingEvents.Skip(start), e => Assert.Equal("script-1", e.Params!["requestId"]!.GetValue<string>()));
        Assert.Equal(3, ctx.PendingEvents.Skip(start).Select(e => e.SessionId).Distinct().Count());
    }

    [Fact]
    public void An_intercepted_request_is_announced_to_observers_but_paused_only_for_the_driver()
    {
        (CdpContext ctx, string page, _) = Fixture();
        var reply = Channel.CreateUnbounded<string>();
        var paused = new Dictionary<string, PausedIntercept>();
        var intercepted = new InterceptedRequest
        {
            RequestId = "intercept-1",
            PageId = page,
            Url = "https://example.test/api",
            Method = "GET",
            Headers = new Dictionary<string, string>(),
            ResourceType = "Fetch",
        };
        ServerSupport.EmitInterceptedRequest(
            intercepted, "frame-1", "driver", reply.Writer, paused,
            observers: Domains.Page.NetworkObserverSessions(ctx, page, "driver"));
        List<JsonNode> messages = [];
        while (reply.Reader.TryRead(out string? text))
        {
            messages.Add(JsonNode.Parse(text)!);
        }
        Assert.Equal(
            ["Network.requestWillBeSent driver", "Network.requestWillBeSent observer-a", "Network.requestWillBeSent observer-b", "Fetch.requestPaused driver"],
            messages.Select(m => m["method"]!.GetValue<string>() + " " + m["sessionId"]!.GetValue<string>()));
    }
}

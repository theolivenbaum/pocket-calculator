using System.Text.Json.Nodes;
using PocketCalculator.Browser;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// The xUnit ports of <c>crates/obscura-cdp/tests/lifecycle_replay.rs</c> (upstream 1959c5e)
/// and of the subscription test in <c>document_write_lifecycle.rs</c> (upstream 3f8a8d1).
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class LifecycleReplay
{
    private static async Task<JsonNode> CallAsync(CdpContext ctx, string method, JsonNode parameters, string session)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = method, Params = parameters, SessionId = session }, ctx);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Message}");
        return response.Result!;
    }

    private static List<JsonNode> Replay(CdpContext ctx, string session, JsonNode frame) =>
        [.. ctx.PendingEvents.Select(e =>
        {
            Assert.Equal("Page.lifecycleEvent", e.Method);
            Assert.Equal(session, e.SessionId);
            Assert.Equal(frame["id"]!.GetValue<string>(), e.Params!["frameId"]!.GetValue<string>());
            Assert.Equal(frame["loaderId"]!.GetValue<string>(), e.Params!["loaderId"]!.GetValue<string>());
            Assert.True(double.IsFinite(e.Params!["timestamp"]!.GetValue<double>()));
            return e.Params!;
        })];

    private static string[] Names(IEnumerable<JsonNode> events) =>
        [.. events.Select(e => e["name"]!.GetValue<string>())];

    private static async Task<JsonNode> FrameAsync(CdpContext ctx, string session) =>
        (await CallAsync(ctx, "Page.getFrameTree", new JsonObject(), session))["frameTree"]!["frame"]!;

    [Fact]
    public async Task Late_lifecycle_subscription_replays_only_to_the_attached_session()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions["managed"] = page;
        await CallAsync(ctx, "Page.enable", new JsonObject(), "managed");
        ctx.Sessions["attached"] = page;
        JsonNode frame = await FrameAsync(ctx, "attached");
        ctx.PendingEvents.Clear();
        await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, "attached");
        List<JsonNode> first = Replay(ctx, "attached", frame);
        Assert.Equal(["commit", "DOMContentLoaded", "load", "networkAlmostIdle", "networkIdle"], Names(first));

        // Chrome replays on each enable, retaining the document's original timings.
        foreach (bool enabled in new[] { true, false, true })
        {
            ctx.PendingEvents.Clear();
            await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = enabled }, "attached");
            List<JsonNode> events = Replay(ctx, "attached", frame);
            Assert.Equal(
                enabled ? first.Select(e => e.ToJsonString()) : [],
                events.Select(e => e.ToJsonString()));
        }
    }

    [Fact]
    public async Task Lifecycle_replay_tracks_the_latest_navigation_and_original_timestamps()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions["driver"] = page;
        ctx.Sessions["observer"] = page;
        await CallAsync(ctx, "Page.enable", new JsonObject(), "driver");
        JsonNode initial = await FrameAsync(ctx, "observer");
        foreach (string url in new[] { "data:text/html,<p>first</p>", "data:text/html,<p>second</p>" })
        {
            ctx.PendingEvents.Clear();
            await CallAsync(ctx, "Page.navigate", new JsonObject { ["url"] = url }, "driver");
            List<string> emitted = [.. ctx.PendingEvents
                .Where(e => e.Method == "Page.lifecycleEvent" && e.Params!["name"]!.GetValue<string>() != "init")
                .Select(e => e.Params!.ToJsonString())];
            JsonNode frame = await FrameAsync(ctx, "observer");
            Assert.NotEqual(initial["loaderId"]!.GetValue<string>(), frame["loaderId"]!.GetValue<string>());
            ctx.PendingEvents.Clear();
            await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, "observer");
            List<string> events = [.. Replay(ctx, "observer", frame).Select(e => e.ToJsonString())];
            Assert.NotEmpty(events);
            foreach (string original in emitted)
            {
                Assert.Contains(original, events);
            }
        }
    }

    // Upstream asserts [commit, DOMContentLoaded, load]: its document.open() reports no idle.
    // The port reports networkIdle after a reopened document's load (Dispatcher.DrainDocumentLoads,
    // for Playwright's setContent), so the replay carries it too, with the new document's time.
    [Fact]
    public async Task Document_replacement_replays_the_new_document_without_a_subscriber()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions["writer"] = page;
        await CallAsync(ctx, "Page.enable", new JsonObject(), "writer");
        await CallAsync(ctx, "Page.navigate", new JsonObject { ["url"] = "data:text/html,<p>old</p>" }, "writer");
        double navigated = ctx.LifecycleTimestamps[page][2]!.Value;
        await Task.Delay(20);
        await CallAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "document.open(); document.write('<p>replacement</p>'); document.close()",
        }, "writer");
        await CallAsync(ctx, "Runtime.evaluate", new JsonObject { ["expression"] = "document.readyState" }, "writer");
        JsonNode frame = await FrameAsync(ctx, "writer");
        ctx.PendingEvents.Clear();
        await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, "writer");
        List<JsonNode> events = Replay(ctx, "writer", frame);
        Assert.Equal(["commit", "DOMContentLoaded", "load", "networkAlmostIdle", "networkIdle"], Names(events));
        Assert.True(events[2]["timestamp"]!.GetValue<double>() > navigated, "load must be the new document's");
    }

    [Fact]
    public async Task Failed_navigation_does_not_replay_completion_from_the_previous_document()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions["observer"] = page;
        await CallAsync(ctx, "Page.enable", new JsonObject(), "observer");
        string frameId = ctx.GetPage(page)!.FrameId;
        Domains.Page.EmitNavigationEvents(
            ctx, "observer", frameId, "failed-loader", "https://invalid.test/", page, [],
            WaitUntil.Load, false, DocumentReadiness.Committed);
        JsonNode frame = await FrameAsync(ctx, "observer");
        ctx.PendingEvents.Clear();
        await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, "observer");
        Assert.Equal(["commit"], Names(Replay(ctx, "observer", frame)));
    }

    // Upstream 3f8a8d1: document_lifecycle_subscriptions_are_independent_of_runtime.
    [Fact]
    public async Task Document_lifecycle_subscriptions_are_independent_of_runtime()
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions["lifecycle"] = page;
        ctx.Sessions["console"] = page;
        await CallAsync(ctx, "Page.enable", new JsonObject(), "lifecycle");
        await CallAsync(ctx, "Page.navigate", new JsonObject { ["url"] = "data:text/html,<p>old</p>" }, "lifecycle");
        await CallAsync(ctx, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, "lifecycle");

        for (int phase = 0; phase < 3; phase++)
        {
            if (phase == 1)
            {
                await CallAsync(ctx, "Runtime.enable", new JsonObject(), "console");
            }
            else if (phase == 2)
            {
                await CallAsync(ctx, "Runtime.disable", new JsonObject(), "console");
            }
            ctx.PendingEvents.Clear();
            await CallAsync(ctx, "Runtime.evaluate", new JsonObject
            {
                ["expression"] = "(() => { document.open(); console.debug('subscription-marker'); document.write('<p>replacement</p>'); document.close(); })()",
            }, "lifecycle");
            await CallAsync(ctx, "Runtime.evaluate", new JsonObject { ["expression"] = "document.readyState" }, "lifecycle");
            List<(string? Session, string Name)> lifecycle = [.. ctx.PendingEvents
                .Where(e => e.Method == "Page.lifecycleEvent")
                .Select(e => (e.SessionId, e.Params!["name"]!.GetValue<string>()))];
            Assert.Equal(
                [("lifecycle", "init"), ("lifecycle", "DOMContentLoaded"), ("lifecycle", "load"), ("lifecycle", "networkIdle")],
                lifecycle);
        }
    }

    [Fact]
    public async Task Set_lifecycle_events_enabled_validates_its_argument()
    {
        var ctx = CdpContext.New();
        ctx.Sessions["s"] = ctx.CreatePage();
        CdpResponse missing = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = "Page.setLifecycleEventsEnabled", Params = new JsonObject(), SessionId = "s" }, ctx);
        Assert.Equal("enabled must be a boolean", missing.Error?.Message);
        Assert.Null(ServerSupport.FastPathResponse(
            """{"id":1,"method":"Page.setLifecycleEventsEnabled","params":{"enabled":true},"sessionId":"s"}"""));
    }
}

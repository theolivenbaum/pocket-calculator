using System.Text.Json.Nodes;

using Xunit;

using RuntimeDomain = PocketCalculator.Cdp.Domains.Runtime;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Ports of upstream 3dea548's CDP tests: an IndexedDB upgrade on one page of a connection
/// while another page of it holds a connection to the database.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-cdp (with_live_peers in domains/runtime.rs, the idle-page
/// re-arm in server.rs): upstream keeps every page of a connection live (#756), so the page
/// holding the connection has to be pumped while the other awaits, to deliver versionchange
/// and run its delayed close. A C# connection keeps one page's runtime live
/// (CdpContext.GetSessionPageMut suspends the others), and suspending a page disposes its
/// realm, which releases its IndexedDB connections and transactions (IndexedDbOwner), as a
/// closed document does in Chromium. The upgrade then goes ahead without waiting; a
/// connection's IndexedDB is its own (BrowserContext.ConnectionCopy), so no other
/// connection's page can hold one either. The tests keep upstream's outcome.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class IndexedDbPeerPages
{
    private static async Task<List<string>> TwoPagesAsync(CdpContext ctx, CdpTestServer server)
    {
        List<string> sessions = [];
        for (var i = 0; i < 2; i++)
        {
            var id = ctx.CreatePage();
            var session = $"{id}-session";
            ctx.Sessions[session] = id;
            CdpDomainFixtures.Unwrap(await Domains.Page.HandleAsync(
                "navigate", new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" }, ctx, session));
            sessions.Add(session);
        }
        return sessions;
    }

    private const string HoldConnection = """
        (() => {
            globalThis.opened = false;
            const r = indexedDB.open('peer',1);
            r.onupgradeneeded = () => r.result.createObjectStore('items');
            r.onsuccess = () => {globalThis.opened = true; globalThis.peer = r.result;
                peer.onversionchange = () => setTimeout(() => peer.close(),10);};
        })()
        """;

    // crates/obscura-cdp/src/domains/runtime.rs:
    // indexeddb_awaited_commands_deliver_peer_versionchange_and_delayed_close
    [Theory]
    [InlineData("evaluate")]
    [InlineData("callFunctionOn")]
    public async Task indexeddb_awaited_commands_deliver_peer_versionchange_and_delayed_close(string method)
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        using var server = CdpTestServer.ServeHtml("<!doctype html><body>idb</body>");
        var sessions = await TwoPagesAsync(ctx, server);
        var first = ctx.GetSessionPageMut(sessions[0])!;
        first.Evaluate(HoldConnection);
        await first.Js!.RunEventLoopBoundedAsync(200);
        Assert.Equal("true", first.Evaluate("opened")?.ToJsonString());
        const string Body = "return new Promise((resolve,reject)=>{const r=indexedDB.open('peer',2);r.onblocked=()=>reject(new Error('blocked'));r.onsuccess=()=>{r.result.close();resolve(r.result.version)};r.onerror=()=>reject(r.error)})";
        var parameters = new JsonObject { ["awaitPromise"] = true, ["returnByValue"] = true, ["timeout"] = 1000 };
        if (method == "evaluate")
        {
            parameters["expression"] = "(()=>{" + Body + "})()";
        }
        else
        {
            parameters["functionDeclaration"] = "function(){" + Body + "}";
        }
        var result = CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(method, parameters, ctx, sessions[1]));
        Assert.Equal(2.0, result["result"]?["value"]?.GetValue<double>());
    }

    // crates/obscura-cdp/src/server.rs:
    // indexeddb_versionchange_rearms_an_idle_page_until_its_delayed_close, driven by the C#
    // processor's pump turn.
    [Fact]
    public async Task indexeddb_versionchange_rearms_an_idle_page_until_its_delayed_close()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        using var server = CdpTestServer.ServeHtml("<!doctype html><body>idb</body>");
        var sessions = await TwoPagesAsync(ctx, server);
        var first = ctx.GetSessionPageMut(sessions[0])!;
        first.Evaluate(HoldConnection);
        var deadline = Environment.TickCount64 + 1000;
        while (!await CdpServer.PumpLivePageEventLoopAsync(ctx) && Environment.TickCount64 < deadline)
        {
        }
        Assert.Equal("true", first.Evaluate("opened")?.ToJsonString());
        var second = ctx.GetSessionPageMut(sessions[1])!;
        second.Evaluate("""
            (() => {
                globalThis.upgraded = false;
                setTimeout(() => {const r = indexedDB.open('peer',2);
                    r.onsuccess = () => globalThis.upgraded = r.result.version === 2;},10);
            })()
            """);
        deadline = Environment.TickCount64 + 2000;
        var upgraded = false;
        while (Environment.TickCount64 < deadline)
        {
            await CdpServer.PumpLivePageEventLoopAsync(ctx);
            if (second.Evaluate("upgraded")?.ToJsonString() == "true")
            {
                upgraded = true;
                break;
            }
        }
        Assert.True(upgraded, "the suspended page's connection must not hold the upgrade");
    }
}

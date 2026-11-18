using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Browser.Tests;

// Port of upstream 359362f's dynamic_resource_destinations_reach_network_events_and_callbacks
// (runtime.rs): a dynamic script, a fetch() and an XHR of the same URL report their own
// destinations to the network events and the request/response callbacks.
public sealed class DynamicResourceDestinations
{
    private const string Body = "globalThis.destinationLoaded = 1;";

    [Fact]
    public async Task Dynamic_resource_destinations_reach_network_events_and_callbacks()
    {
        using var server = TestHttpServer.Start(request => request.Path == "/same.js"
            ? TestResponse.JavaScript(Body)
            : TestResponse.Html("<html><head></head><body></body></html>"));
        using Page page = PageFixtures.NewPage("destinations");
        var requests = new List<ResourceType>();
        var responses = new List<ResourceType>();
        page.OnRequest(request =>
        {
            if (request.Url.AbsolutePath == "/same.js")
            {
                lock (requests) { requests.Add(request.ResourceType); }
            }
        });
        page.OnResponse((request, _) =>
        {
            if (request.Url.AbsolutePath == "/same.js")
            {
                lock (responses) { responses.Add(request.ResourceType); }
            }
        });
        await page.NavigateAsync($"{server.Origin}/");

        RemoteObjectInfo result = await page.EvaluateForCdpAsync(
            """
            (async () => {
                await new Promise((resolve, reject) => {
                    const script = document.createElement('script');
                    script.src = '/same.js';
                    script.onload = resolve; script.onerror = reject;
                    document.head.append(script);
                });
                const fetched = await (await fetch('/same.js')).text();
                const xhrBody = await new Promise((resolve, reject) => {
                    const xhr = new XMLHttpRequest(); xhr.open('GET', '/same.js');
                    xhr.onload = () => resolve(xhr.responseText); xhr.onerror = reject; xhr.send();
                });
                return JSON.stringify([globalThis.destinationLoaded, fetched, xhrBody]);
            })()
            """,
            true,
            true);
        Assert.Equal($"[1,\"{Body}\",\"{Body}\"]", result.Value?.GetValue<string>());

        page.SyncJsNetworkEvents();
        Assert.Equal<string>(
            ["Script", "Fetch", "XHR"],
            page.NetworkEvents.Where(e => e.Url.EndsWith("/same.js", StringComparison.Ordinal))
                .Select(e => e.ResourceType).ToList());
        Assert.Equal<ResourceType>([ResourceType.Script, ResourceType.Fetch, ResourceType.Xhr], requests);
        Assert.Equal<ResourceType>([ResourceType.Script, ResourceType.Fetch, ResourceType.Xhr], responses);
    }
}

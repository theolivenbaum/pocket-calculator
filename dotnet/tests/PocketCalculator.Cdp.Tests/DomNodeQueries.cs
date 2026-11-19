using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/dom_node_queries.rs</c> (upstream eb2b515):
/// <c>DOM.querySelector[All]</c> searches the requested node's subtree.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class DomNodeQueries
{
    private const string Session = "dom-query-session";

    private static Task<CdpResponse> CallAsync(CdpContext ctx, string method, JsonNode parameters) =>
        Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = method, Params = parameters, SessionId = Session }, ctx);

    private static async Task<JsonNode> OkAsync(CdpContext ctx, string method, JsonNode parameters)
    {
        CdpResponse response = await CallAsync(ctx, method, parameters);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Message}");
        return response.Result!;
    }

    private static async Task<JsonNode?> QueryAsync(CdpContext ctx, JsonNode? nodeId, string selector)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (nodeId is not null)
        {
            parameters["nodeId"] = nodeId.DeepClone();
        }
        return (await OkAsync(ctx, "DOM.querySelector", parameters))["nodeId"];
    }

    private static async Task<string> QueryAllAsync(CdpContext ctx, JsonNode nodeId, string selector) =>
        (await OkAsync(ctx, "DOM.querySelectorAll", new JsonObject { ["nodeId"] = nodeId.DeepClone(), ["selector"] = selector }))["nodeIds"]!.ToJsonString();

    [Fact]
    public async Task Dom_queries_respect_the_requested_subtree_and_shadow_root()
    {
        var ctx = CdpContext.New();
        ctx.Sessions[Session] = ctx.CreatePage();
        await OkAsync(ctx, "Page.navigate", new JsonObject { ["url"] = "about:blank" });
        await OkAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = """
                (() => {
                    document.body.innerHTML =
                        '<section id="left"><button class="hit" id="left-hit">Left</button><div id="host"></div></section>' +
                        '<section id="right"><button class="hit" id="right-hit">Right</button></section>';
                    const shadow = document.querySelector('#host').attachShadow({mode: 'open'});
                    shadow.innerHTML = '<button class="hit" id="shadow-hit">Shadow</button>';
                })()
                """,
        });
        // DOM.getDocument reports no shadowRoots yet (upstream 717dd92 is open), so the shadow
        // root's id comes from DOM.requestNode on its remote object.
        JsonNode remote = await OkAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "document.querySelector('#host').shadowRoot",
        });
        JsonNode shadow = (await OkAsync(ctx, "DOM.requestNode", new JsonObject
        {
            ["objectId"] = remote["result"]!["objectId"]!.DeepClone(),
        }))["nodeId"]!;

        JsonNode left = (await QueryAsync(ctx, null, "#left"))!;
        JsonNode right = (await QueryAsync(ctx, null, "#right"))!;
        JsonNode leftHit = (await QueryAsync(ctx, null, "#left-hit"))!;
        JsonNode rightHit = (await QueryAsync(ctx, null, "#right-hit"))!;
        JsonNode shadowHit = (await QueryAsync(ctx, shadow, "#shadow-hit"))!;

        Assert.Equal(leftHit.ToJsonString(), (await QueryAsync(ctx, left, ".hit"))!.ToJsonString());
        Assert.Equal(rightHit.ToJsonString(), (await QueryAsync(ctx, right, ".hit"))!.ToJsonString());
        Assert.Equal($"[{leftHit.ToJsonString()}]", await QueryAllAsync(ctx, left, ".hit"));
        Assert.Equal($"[{rightHit.ToJsonString()}]", await QueryAllAsync(ctx, right, ".hit"));
        Assert.Equal(rightHit.ToJsonString(), (await QueryAsync(ctx, right, ":scope > .hit"))!.ToJsonString());
        Assert.Equal($"[{rightHit.ToJsonString()}]", await QueryAllAsync(ctx, right, ":scope > .hit"));
        Assert.Equal("0", (await QueryAsync(ctx, left, "#right-hit"))!.ToJsonString());
        Assert.Equal("0", (await QueryAsync(ctx, left, "#left"))!.ToJsonString());
        Assert.Equal(shadowHit.ToJsonString(), (await QueryAsync(ctx, shadow, ".hit"))!.ToJsonString());
        Assert.Equal($"[{shadowHit.ToJsonString()}]", await QueryAllAsync(ctx, shadow, ".hit"));
        Assert.Equal("0", (await QueryAsync(ctx, left, "#shadow-hit"))!.ToJsonString());
        Assert.Equal($"[{leftHit.ToJsonString()},{rightHit.ToJsonString()}]", await QueryAllAsync(ctx, 0, ".hit"));

        JsonNode?[] invalid = [999999, 4294967297UL, "invalid", -1, 1.5, null, true];
        foreach (JsonNode? nodeId in invalid)
        {
            foreach (string method in new[] { "DOM.querySelector", "DOM.querySelectorAll" })
            {
                var parameters = new JsonObject { ["nodeId"] = nodeId?.DeepClone(), ["selector"] = ".hit" };
                Assert.True((await CallAsync(ctx, method, parameters)).Error is not null,
                    $"invalid nodeId {nodeId?.ToJsonString() ?? "null"} should be rejected for {method}");
            }
        }
        foreach (string method in new[] { "DOM.querySelector", "DOM.querySelectorAll" })
        {
            var parameters = new JsonObject { ["nodeId"] = right.DeepClone(), ["selector"] = "[" };
            Assert.True((await CallAsync(ctx, method, parameters)).Error is not null,
                $"invalid selectors must not become empty results for {method}");
        }
    }
}

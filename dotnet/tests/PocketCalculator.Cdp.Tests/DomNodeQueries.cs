using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/dom_node_queries.rs</c>: light-DOM node
/// queries and <c>DOM.setChildNodes</c> pushes (upstream 717dd92), and
/// <c>DOM.querySelector[All]</c> searching the requested node's subtree (upstream eb2b515).
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class DomNodeQueries
{
    private const string Session = "dom-query-session";

    private const string Fixture =
        "data:text/html,<main><label id=lab for=c style='position:absolute;left:32px;top:48px;width:120px;height:32px'>agree</label><input id=c></main>";

    private static Task<CdpResponse> CallAsync(CdpContext ctx, string method, JsonNode parameters, string session = Session) =>
        Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = method, Params = parameters, SessionId = session }, ctx);

    private static async Task<JsonNode> OkAsync(CdpContext ctx, string method, JsonNode parameters, string session = Session)
    {
        CdpResponse response = await CallAsync(ctx, method, parameters, session);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Message}");
        return response.Result!;
    }

    private static async Task<(CdpContext Ctx, string Page)> FixtureAsync(string url = Fixture)
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions[Session] = page;
        await OkAsync(ctx, "Page.navigate", new JsonObject { ["url"] = url });
        ctx.PendingEvents.Clear();
        return (ctx, page);
    }

    private static string ObjectId(CdpContext ctx, string expression, string session = Session) =>
        ctx.GetSessionPageMut(session)!.Js!.StoreObjectWithMeta(expression).ObjectId!;

    private static List<CdpEvent> Pushes(CdpContext ctx) =>
        ctx.PendingEvents.Where(e => e.Method == "DOM.setChildNodes").ToList();

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

    private static JsonNode? Find(JsonNode? root, Func<JsonNode, bool> match)
    {
        var pending = new Stack<JsonNode?>([root]);
        while (pending.TryPop(out JsonNode? node))
        {
            if (node is null)
            {
                continue;
            }
            if (match(node))
            {
                return node;
            }
            foreach (string key in new[] { "children", "shadowRoots" })
            {
                foreach (JsonNode? child in node[key] as JsonArray ?? [])
                {
                    pending.Push(child);
                }
            }
        }
        return null;
    }

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
        // Upstream reads the shadow root's id from the page (`shadow._nid`). The port reports
        // shadowRoots in DOM.getDocument, as Chromium does, so the id comes from there.
        JsonNode document = await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = -1 });
        JsonNode host = Find(document["root"], node => node["attributes"] is JsonArray { Count: >= 2 } attributes
            && attributes[1]?.GetValue<string>() == "host")!;
        JsonNode shadow = host["shadowRoots"]![0]!["nodeId"]!;

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

    [Fact]
    public async Task Dom_attributes_preserve_qualified_names_and_reject_invalid_nodes()
    {
        (CdpContext ctx, _) = await FixtureAsync();
        JsonNode node = (await OkAsync(ctx, "DOM.querySelector", new JsonObject { ["selector"] = "#lab" }))["nodeId"]!;
        ctx.GetSessionPageMut(Session)!.Evaluate(
            "document.querySelector('#lab').setAttributeNS('urn:test','x:flag','yes')");
        JsonArray attributes = (await OkAsync(ctx, "DOM.getAttributes", new JsonObject { ["nodeId"] = node.DeepClone() }))["attributes"]!.AsArray();
        var pairs = new Dictionary<string, string>();
        for (int i = 0; i + 1 < attributes.Count; i += 2)
        {
            pairs[attributes[i]!.GetValue<string>()] = attributes[i + 1]!.GetValue<string>();
        }
        Assert.Equal("lab", pairs["id"]);
        Assert.Equal("c", pairs["for"]);
        Assert.Equal("yes", pairs["x:flag"]);
        foreach (JsonObject invalid in new[] { new JsonObject(), new JsonObject { ["nodeId"] = 999999 }, new JsonObject { ["nodeId"] = 4294967297UL } })
        {
            Assert.True((await CallAsync(ctx, "DOM.getAttributes", invalid)).Error is not null);
        }

        // describeNode carries the same qualified name.
        JsonNode described = await OkAsync(ctx, "DOM.describeNode", new JsonObject { ["nodeId"] = node.DeepClone() });
        Assert.Contains("x:flag", described["node"]!["attributes"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task Dom_request_node_pushes_ancestors_once_to_the_requesting_session()
    {
        (CdpContext ctx, string page) = await FixtureAsync();
        string oid = ObjectId(ctx, "document.querySelector('#lab')");
        Assert.Equal(0UL, (await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = oid }))["nodeId"].AsU64());
        await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 });
        ctx.PendingEvents.Clear();
        JsonNode node = (await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = oid }))["nodeId"]!;
        Assert.NotEqual(0UL, node.AsU64());
        List<CdpEvent> pushes = Pushes(ctx);
        Assert.NotEmpty(pushes);
        Assert.All(pushes, e => Assert.Equal(Session, e.SessionId));
        Assert.Contains(pushes, e => e.Params!["nodes"]!.AsArray().Any(child =>
            child!["nodeId"].AsU64() == node.AsU64() && child["nodeName"].AsString() == "LABEL"));
        ctx.PendingEvents.Clear();
        await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = oid });
        Assert.True(ctx.PendingEvents.Count == 0, "known nodes must not be pushed again");

        ctx.Sessions["other"] = page;
        string otherOid = ObjectId(ctx, "document.querySelector('#lab')", "other");
        Assert.Equal(0UL, (await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = otherOid }, "other"))["nodeId"].AsU64());
        await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 }, "other");
        Assert.NotEqual(0UL, (await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = otherOid }, "other"))["nodeId"].AsU64());
        Assert.Contains(ctx.PendingEvents, e => e.Method == "DOM.setChildNodes" && e.SessionId == "other");

        foreach (string expression in new[] { "({})", "({_nid:1})" })
        {
            string invalid = ObjectId(ctx, expression);
            Assert.True((await CallAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = invalid })).Error is not null, expression);
        }
        Assert.True((await CallAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = "missing" })).Error is not null);
        await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = -1 });
        string inserted = ObjectId(ctx, "document.querySelector('main').appendChild(document.createElement('button'))");
        Assert.True((await CallAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = inserted })).Error is not null,
            "without mutation events, do not return a frontend ID that was never published");
    }

    /// <summary>
    /// Port addition: a node in an open shadow tree is pushed through its shadow root, as
    /// Chromium does (upstream refuses every node outside the light DOM).
    /// </summary>
    [Fact]
    public async Task Dom_request_node_pushes_through_shadow_roots()
    {
        (CdpContext ctx, _) = await FixtureAsync();
        ctx.GetSessionPageMut(Session)!.Evaluate(
            "document.querySelector('main').attachShadow({mode:'open'}).innerHTML='<button>shadow<b>deep</b></button>'");
        JsonNode document = await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 });
        ctx.PendingEvents.Clear();
        string oid = ObjectId(ctx, "document.querySelector('main').shadowRoot.querySelector('b')");
        ulong node = (await OkAsync(ctx, "DOM.requestNode", new JsonObject { ["objectId"] = oid }))["nodeId"].AsU64()!.Value;
        Assert.NotEqual(0UL, node);
        List<CdpEvent> pushes = Pushes(ctx);
        JsonNode main = pushes.SelectMany(e => e.Params!["nodes"]!.AsArray()).First(n => n!["localName"].AsString() == "main")!;
        JsonNode root = main["shadowRoots"]![0]!;
        Assert.Equal(11L, root["nodeType"].AsI64());
        Assert.Equal("#document-fragment", root["nodeName"].AsString());
        Assert.Equal("open", root["shadowRootType"].AsString());
        Assert.Null(root["children"]);
        Assert.Contains(pushes, e => e.Params!["parentId"].AsU64() == root["nodeId"].AsU64());
        Assert.Contains(pushes, e => e.Params!["nodes"]!.AsArray().Any(n => n!["nodeId"].AsU64() == node));
        Assert.NotNull(document["root"]);
    }

    [Fact]
    public async Task Dom_child_requests_expand_depth_without_repeating_known_children()
    {
        (CdpContext ctx, _) = await FixtureAsync();
        JsonNode root = (await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 }))["root"]!["nodeId"]!;
        ctx.PendingEvents.Clear();
        Assert.Equal("{}", (await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = 2 })).ToJsonString());
        CdpEvent pushed = ctx.PendingEvents.First(e => e.Method == "DOM.setChildNodes");
        Assert.Equal(root.AsU64(), pushed.Params!["parentId"].AsU64());
        JsonNode html = pushed.Params["nodes"]![0]!;
        Assert.Equal("HTML", html["nodeName"].AsString());
        Assert.Equal("BODY", html["children"]![1]!["nodeName"].AsString());
        ctx.PendingEvents.Clear();
        await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = 2 });
        Assert.Empty(ctx.PendingEvents);
        await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = -1 });
        Assert.Contains(ctx.PendingEvents, e => e.Method == "DOM.setChildNodes");
        foreach (JsonNode depth in new JsonNode[] { 0, -2, "bad" })
        {
            Assert.True((await CallAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = depth })).Error is not null);
        }
    }

    [Fact]
    public async Task Dom_child_requests_allow_pierce_on_plain_trees_but_reject_unserved_roots()
    {
        (CdpContext ctx, _) = await FixtureAsync();
        JsonNode root = (await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 }))["root"]!["nodeId"]!;
        await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = -1, ["pierce"] = true });
        Assert.Contains(ctx.PendingEvents, e => e.Method == "DOM.setChildNodes");
        ctx.GetSessionPageMut(Session)!.Evaluate(
            "document.querySelector('main').attachShadow({mode:'open'}).innerHTML='<button>shadow</button>'");
        Assert.True((await CallAsync(ctx, "DOM.requestChildNodes",
            new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = -1, ["pierce"] = true })).Error is not null,
            "a requested shadow tree must not be silently omitted");

        // Port addition: a shadow root the session was given is served, as Chromium serves it.
        root = (await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 }))["root"]!["nodeId"]!;
        ctx.PendingEvents.Clear();
        await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = -1 });
        Assert.All(Pushes(ctx).SelectMany(e => e.Params!["nodes"]!.AsArray()),
            n => Assert.Null(Find(n, node => node["localName"].AsString() == "button")));
        ctx.PendingEvents.Clear();
        await OkAsync(ctx, "DOM.requestChildNodes", new JsonObject { ["nodeId"] = root.DeepClone(), ["depth"] = -1, ["pierce"] = true });
        List<CdpEvent> pushes = Pushes(ctx);
        Assert.Single(pushes);
        Assert.Equal("BUTTON", pushes[0].Params!["nodes"]![0]!["nodeName"].AsString());
    }

    /// <summary>
    /// Port addition: <c>DOM.getDocument</c> reports a host's shadow roots, open and closed,
    /// and expands them only with <c>pierce</c> (measured against Chromium 1243).
    /// </summary>
    [Fact]
    public async Task Dom_get_document_reports_shadow_roots_and_pierces_them_on_request()
    {
        (CdpContext ctx, _) = await FixtureAsync("data:text/html,<main><p>x</p></main><div></div>");
        ctx.GetSessionPageMut(Session)!.Evaluate(
            "(() => { document.querySelector('main').attachShadow({mode:'open'}).innerHTML='<button>shadow</button>';"
            + " document.querySelector('div').attachShadow({mode:'closed'}).innerHTML='<i>c</i>'; })()");
        foreach (bool pierce in new[] { false, true })
        {
            JsonNode document = await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = -1, ["pierce"] = pierce });
            JsonNode main = Find(document["root"], n => n["localName"].AsString() == "main")!;
            JsonNode div = Find(document["root"], n => n["localName"].AsString() == "div")!;
            Assert.Equal("P", main["children"]![0]!["nodeName"].AsString());
            JsonNode open = main["shadowRoots"]![0]!;
            JsonNode closed = div["shadowRoots"]![0]!;
            Assert.Equal("open", open["shadowRootType"].AsString());
            Assert.Equal("closed", closed["shadowRootType"].AsString());
            Assert.Equal(1L, open["childNodeCount"].AsI64());
            Assert.Equal(pierce ? "BUTTON" : null, open["children"]?[0]?["nodeName"].AsString());
            Assert.Equal(pierce ? "I" : null, closed["children"]?[0]?["nodeName"].AsString());
        }
    }

    [Fact]
    public async Task Dom_hit_lookup_reports_frame_and_only_publishes_requested_node_ids()
    {
        (CdpContext ctx, string page) = await FixtureAsync();
        var parameters = new JsonObject { ["x"] = 64, ["y"] = 64 };
        JsonNode before = await OkAsync(ctx, "DOM.getNodeForLocation", parameters.DeepClone());
        Assert.Null(before["nodeId"]);
        Assert.NotNull(before["backendNodeId"].AsU64());
        Assert.Equal(ctx.GetPage(page)!.FrameId, before["frameId"].AsString());
        await OkAsync(ctx, "DOM.getDocument", new JsonObject { ["depth"] = 0 });
        JsonNode after = await OkAsync(ctx, "DOM.getNodeForLocation", parameters.DeepClone());
        JsonNode description = await OkAsync(ctx, "DOM.describeNode", new JsonObject { ["backendNodeId"] = after["backendNodeId"]!.DeepClone() });
        Assert.Equal("LABEL", description["node"]!["nodeName"].AsString());
        Assert.Equal(description["node"]!["nodeId"].AsU64(), after["nodeId"].AsU64());
        Assert.True((await CallAsync(ctx, "DOM.getNodeForLocation", new JsonObject { ["x"] = -1, ["y"] = 64 })).Error is not null);
        await OkAsync(ctx, "DOM.disable", new JsonObject());
        Assert.Null((await OkAsync(ctx, "DOM.getNodeForLocation", parameters.DeepClone()))["nodeId"]);
    }
}

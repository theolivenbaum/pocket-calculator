using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/accessibility_names.rs</c> (upstream f81c296).
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class AccessibilityNames
{
    private const string Session = "ax-session";

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, string method, JsonNode parameters)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = 1, Method = method, Params = parameters, SessionId = Session },
            ctx);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Message}");
        return response.Result!;
    }

    private static Task<JsonNode> EvaluateAsync(CdpContext ctx, string expression) =>
        CdpAsync(ctx, "Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true });

    private static async Task<CdpContext> SetupAsync(string html)
    {
        var ctx = CdpContext.New();
        string page = ctx.CreatePage();
        ctx.Sessions[Session] = page;
        await CdpAsync(ctx, "Page.navigate", new JsonObject { ["url"] = "about:blank" });
        await EvaluateAsync(ctx, $"document.body.innerHTML = {CdpJson.String(html)}");
        return ctx;
    }

    private static async Task<JsonArray> TreeAsync(CdpContext ctx) =>
        (await CdpAsync(ctx, "Accessibility.getFullAXTree", new JsonObject()))["nodes"]!.AsArray();

    private static string? Str(JsonNode? node, string key, string inner = "value") =>
        node?[key]?[inner]?.GetValue<string>();

    private static bool Has(JsonArray nodes, string role, string name) =>
        nodes.Any(node => Str(node, "role") == role && Str(node, "name") == name);

    [Fact]
    public async Task AccessibilityNamesUseLiveDomAndBackendIds()
    {
        CdpContext ctx = await SetupAsync(
            """
            <label for="field">Email</label><input id="field">
                    <button id="save">Save <b>changes</b></button><a href="/">Read docs</a>
            """);
        JsonArray nodes = await TreeAsync(ctx);
        foreach ((string role, string name) in new[] { ("textbox", "Email"), ("button", "Save changes"), ("link", "Read docs") })
        {
            Assert.True(Has(nodes, role, name), nodes.ToJsonString());
        }

        JsonNode button = nodes.First(node => Str(node, "role") == "button")!;
        JsonNode resolved = await CdpAsync(
            ctx,
            "DOM.resolveNode",
            new JsonObject { ["backendNodeId"] = button["backendDOMNodeId"]!.DeepClone() });
        Assert.True(resolved["object"]?["objectId"] is JsonValue id && id.TryGetValue(out string? _), resolved.ToJsonString());

        await EvaluateAsync(ctx, "document.getElementById('save').textContent = 'Updated'");
        Assert.True(Has(await TreeAsync(ctx), "button", "Updated"));
    }

    [Fact]
    public async Task AccessibilityLabelsPreserveLinkText()
    {
        CdpContext ctx = await SetupAsync(
            """
            <span id="label">Read <a href="/terms">terms</a></span>
                    <button aria-labelledby="label" aria-label="Fallback">Continue</button>
                    <label for="field">Accept <a href="/terms">terms</a> and <span role="link">privacy</span><select><option>Noise</option></select><span role="button">Nested action</span></label>
                    <input id="field">
            """);
        JsonArray nodes = await TreeAsync(ctx);
        foreach ((string role, string name) in new[] { ("button", "Read terms"), ("textbox", "Accept terms and privacy") })
        {
            Assert.True(Has(nodes, role, name), nodes.ToJsonString());
        }
    }

    [Fact]
    public async Task AccessibilityNamesAndVisibilityUseTheLiveCascade()
    {
        CdpContext ctx = await SetupAsync(
            """
            <style>
                    .gone { display: none !important }
                    .invisible { visibility: hidden }
                    .restored { visibility: visible }
                </style>
                <div class="gone" style="display:block"><button>Display hidden</button></div>
                <div class="invisible">Hidden text<button>Inherited hidden</button><button class="restored">Restored</button></div>
                <button style="opacity:0">Transparent</button>
                <button style="position:absolute;left:-10000px">Offscreen</button>
                <span id="label" class="gone">Hidden <span hidden>reference</span></span>
                <button aria-labelledby="label">Fallback</button>
                <label class="gone" for="field">Hidden native <span hidden>label</span></label><input id="field">
                <button id="dynamic">Before<span class="gone"> noise</span></button>
            """);
        JsonArray nodes = await TreeAsync(ctx);
        List<string> names = nodes
            .Where(node => Str(node, "role") == "button")
            .Select(node => Str(node, "name") ?? string.Empty)
            .ToList();
        Assert.Equal(["Restored", "Transparent", "Offscreen", "Hidden reference", "Before"], names);
        Assert.True(Has(nodes, "textbox", "Hidden native label"));
        foreach (JsonNode? node in nodes)
        {
            if (node!["parentId"]?.GetValue<string>() is { } parentId)
            {
                JsonNode parent = nodes.First(candidate => candidate!["nodeId"]!.GetValue<string>() == parentId)!;
                Assert.Contains(
                    node["nodeId"]!.GetValue<string>(),
                    parent["childIds"]!.AsArray().Select(child => child!.GetValue<string>()));
            }

            if (node["childIds"] is JsonArray children)
            {
                foreach (JsonNode? child in children)
                {
                    JsonNode childNode = nodes.First(candidate =>
                        candidate!["nodeId"]!.GetValue<string>() == child!.GetValue<string>())!;
                    Assert.Equal(node["nodeId"]!.GetValue<string>(), childNode["parentId"]!.GetValue<string>());
                }
            }
        }

        await EvaluateAsync(ctx, "document.getElementById('dynamic').className = 'gone'");
        Assert.DoesNotContain(await TreeAsync(ctx), node => Str(node, "name") == "Before");
    }
}

// upstream aa4b4e7, d132880, 3f7cccd: a canceled keydown suppresses the editing default, a
// canceled beforeinput stops text insertion, and readonly fields take no keyboard edits.
using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

[Collection(CdpDomainCollection.Name)]
public sealed class InputEditingDefaults
{
    private static CdpTestServer ServePage() => CdpTestServer.ServeHtml(
        """
        <html><body>
        <input id="blocked"><input id="vetoed"><input id="plain">
        <textarea id="ro" readonly>keep</textarea><input id="roi" readonly value="keep">
        <script>
        document.getElementById('blocked').addEventListener('keydown', function (e) { e.preventDefault(); });
        document.getElementById('vetoed').addEventListener('beforeinput', function (e) { e.preventDefault(); });
        window.__inputs = [];
        document.getElementById('plain').addEventListener('input', function (e) {
            window.__inputs.push(e.constructor.name + '|' + e.inputType + '|' + e.data);
        });
        </script>
        </body></html>
        """);

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, ulong id, string method, JsonObject parameters, string sessionId)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = id, Method = method, Params = parameters, SessionId = sessionId }, ctx);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    [Fact]
    public async Task EditingDefaultsHonorCancellationAndReadonly()
    {
        using CdpTestServer server = ServePage();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "session-1";
        ctx.Sessions[SessionId] = pageId;
        ulong id = 0;
        await CdpAsync(ctx, ++id, "Page.navigate", new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" }, SessionId);

        async Task Focus(string element) =>
            await CdpAsync(ctx, ++id, "Runtime.evaluate", new JsonObject { ["expression"] = $"document.getElementById('{element}').focus()" }, SessionId);
        async Task Key(string key, string code, string text) =>
            await CdpAsync(ctx, ++id, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = key, ["code"] = code, ["text"] = text }, SessionId);

        await Focus("blocked");
        await Key("a", "KeyA", "a");
        await Focus("vetoed");
        await Key("b", "KeyB", "b");
        await Focus("plain");
        await Key("c", "KeyC", "c");
        await Focus("ro");
        await Key("Enter", "Enter", "\r");
        await Key("Backspace", "Backspace", "");
        await Key("x", "KeyX", "x");
        await Focus("roi");
        await Key("Backspace", "Backspace", "");

        JsonNode result = await CdpAsync(ctx, ++id, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "JSON.stringify(['blocked','vetoed','plain','ro','roi'].map(function (i) { return document.getElementById(i).value; }).concat(window.__inputs))",
            ["returnByValue"] = true,
        }, SessionId);
        Assert.Equal("""["","","c","keep","keep","InputEvent|insertText|c"]""", result["result"]!["value"]!.GetValue<string>());
    }
}

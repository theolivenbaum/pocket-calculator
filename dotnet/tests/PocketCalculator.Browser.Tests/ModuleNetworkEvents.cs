using System.Text;
using Xunit;

namespace PocketCalculator.Browser.Tests;

// Port of upstream 3f37d91's crates/obscura-cdp/tests/module_network_events.rs, at the page
// level: every ES module response is recorded once, with its real status, headers and body,
// whether the graph later fails or not.
public sealed class ModuleNetworkEvents
{
    private const string Root = "import './child.js'; globalThis.rootRan = 1;";
    private const string Child = "globalThis.childRan = 1;";
    private const string Throws = "throw new Error('module failed');";

    private static TestResponse Module(string body, string status = "200 OK") =>
        new("text/javascript", Encoding.UTF8.GetBytes(body), status, [("X-Module-Probe", "observed")]);

    private static TestHttpServer Serve() => TestHttpServer.Start(request => request.Path switch
    {
        "/root.js" => Module(Root, "201 Created"),
        "/child.js" => Module(Child),
        "/missing.js" => Module("not found", "404 Not Found"),
        "/throws.js" => Module(Throws),
        _ => TestResponse.Html(
            "<html><head><script type=module src=/root.js></script>"
            + "<script type=module src=/missing.js></script>"
            + "<script type=module src=/throws.js></script></head><body></body></html>"),
    });

    [Fact]
    public async Task Module_responses_are_recorded_once_with_status_headers_and_body()
    {
        using TestHttpServer server = Serve();
        using Page page = PageFixtures.NewPage("module-network");
        await page.NavigateAsync($"{server.Origin}/");
        page.SyncJsNetworkEvents();

        AssertModule(page, server, "/root.js", 201, Root);
        AssertModule(page, server, "/child.js", 200, Child);
        // Recorded although the graph fails to load or evaluate.
        AssertModule(page, server, "/missing.js", 404, "not found");
        AssertModule(page, server, "/throws.js", 200, Throws);
    }

    // Upstream 73358fd: suspending the runtime keeps the recorded requests and their bodies.
    [Fact]
    public async Task Response_bodies_survive_runtime_suspension()
    {
        using TestHttpServer server = Serve();
        using Page page = PageFixtures.NewPage("module-network-suspend");
        await page.NavigateAsync($"{server.Origin}/");
        _ = await page.EvaluateForCdpAsync("fetch('/child.js').then(r => r.text())", true, true);

        page.SuspendJs();
        Assert.Null(page.Js);

        AssertModule(page, server, "/root.js", 201, Root);
        AssertModule(page, server, "/child.js", 200, Child, resourceType: "Fetch");
    }

    // Upstream 8440fe6: a module request that fails without a response is reported too.
    [Fact]
    public async Task Failed_module_requests_report_a_network_failure()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var deadUrl = $"http://127.0.0.1:{deadPort}/dead.js";
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Html(
            $"<html><head><script type=module src=\"{deadUrl}\"></script></head><body></body></html>"));
        using Page page = PageFixtures.NewPage("module-network-failure");
        await page.NavigateAsync($"{server.Origin}/");
        page.SyncJsNetworkEvents();

        var failure = Assert.Single(page.NetworkEvents, e => e.Url == deadUrl);
        Assert.Equal("Script", failure.ResourceType);
        Assert.Equal(0, failure.Status);
        Assert.Equal("net::ERR_FAILED", failure.ErrorText);
    }

    // Upstream 3a4a0b5 (module_body_buffer_preserves_non_utf8_bytes): bodies are kept as
    // their raw bytes, and the CDP form is produced on read, before and after suspension.
    [Fact]
    public async Task Non_utf8_module_bodies_keep_their_raw_bytes()
    {
        byte[] raw = [.. "// "u8, 0xE9, .. "\nglobalThis.nonUtf8ModuleRan=true;"u8];
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/raw.js" => new TestResponse("text/javascript", raw, "200 OK", []),
            _ => TestResponse.Html("<html><head><script type=module src=/raw.js></script></head><body></body></html>"),
        });
        using Page page = PageFixtures.NewPage("module-network-raw");
        await page.NavigateAsync($"{server.Origin}/");
        page.SyncJsNetworkEvents();
        var id = Assert.Single(page.NetworkEvents, e => e.Url == server.Origin + "/raw.js").RequestId;

        var live = page.GetResponseBody(id);
        Assert.NotNull(live);
        Assert.True(live!.Base64Encoded);
        Assert.Equal(Convert.ToBase64String(raw), live.CdpBody());

        page.SuspendJs();
        var retained = page.GetResponseBody(id);
        Assert.NotNull(retained);
        Assert.True(retained!.Base64Encoded);
        Assert.Equal(raw, retained.Body);
        Assert.Equal(Convert.ToBase64String(raw), retained.CdpBody());
        Assert.Equal(raw, page.TakeResponseBodyRaw(id));
        Assert.Null(page.GetResponseBody(id));
    }

    private static void AssertModule(
        Page page, TestHttpServer server, string path, int status, string body, string resourceType = "Script")
    {
        var events = page.NetworkEvents.Where(e => e.Url == server.Origin + path && e.ResourceType == resourceType).ToList();
        var networkEvent = Assert.Single(events);
        Assert.Equal(status, networkEvent.Status);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), networkEvent.BodySize);
        Assert.Equal(
            "observed",
            networkEvent.ResponseHeaders.First(h => h.Key.Equals("x-module-probe", StringComparison.OrdinalIgnoreCase)).Value);
        var stored = page.GetResponseBody(networkEvent.RequestId);
        Assert.NotNull(stored);
        Assert.Equal(body, stored!.CdpBody());
        Assert.False(stored.Base64Encoded);
    }
}

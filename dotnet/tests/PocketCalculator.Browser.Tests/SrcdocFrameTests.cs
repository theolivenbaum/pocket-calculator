using System.Text.Json.Nodes;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// <c>&lt;iframe srcdoc&gt;</c>, measured on Chromium 141: srcdoc wins over src (src is never
/// requested); the frame is <c>about:srcdoc</c> with its parent's origin, base URL and cookie
/// URL; setting srcdoc navigates the frame, a src change while srcdoc is present does nothing,
/// and removing srcdoc loads src; sandboxed without allow-same-origin the frame has an opaque
/// origin, its <c>contentDocument</c> is null and <c>document.cookie</c> throws SecurityError.
/// The port had no srcdoc: the frame loaded src.
/// </summary>
public sealed class SrcdocFrameTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static TestHttpServer Server(Func<string, TestResponse?> pages) =>
        TestHttpServer.Start(request => pages(request.Path.Split('?')[0]) ?? TestResponse.JavaScript("/* logged */"));

    private static TestRequest Logged(TestHttpServer server, string path)
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            foreach (TestRequest request in server.Requests)
            {
                if (request.Path.Split('?')[0] == path)
                {
                    return request;
                }
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException(
            $"no request for {path}; saw {string.Join(", ", server.Requests.Select(request => request.Path))}");
    }

    private static bool Requested(TestHttpServer server, string path)
    {
        foreach (TestRequest request in server.Requests)
        {
            if (request.Path.Split('?')[0] == path)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Header(TestRequest request, string name)
    {
        foreach (string line in request.Headers.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon], name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static FrameRealm SrcdocFrame(Page page) =>
        Assert.Single(page.Frames, frame => frame.Url == "about:srcdoc");

    private static async Task SettleFramesAsync(Page page)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await page.SettleAsync(50);
        }
    }

    /// <summary>
    /// upstream 9b05070: iframe_realm_node_ops_resolve_the_frame_document. A child realm's
    /// node ids index its own document; a script frame code inserts must run, its canvas must
    /// give a 2D context, and its image must fire load.
    /// </summary>
    [Fact]
    public async Task IframeRealmNodeOpsResolveTheFrameDocument()
    {
        using TestHttpServer server = Server(path => path == "/page"
            ? TestResponse.Html("<html><body><p>parent</p></body></html>")
            : null);
        using Page page = PageFixtures.NewPage("frame-node-ops");
        await page.NavigateAsync($"{server.Origin}/page");
        page.Evaluate("""
            (() => {
                const frame = document.createElement('iframe'); frame.id = 'child';
                frame.srcdoc = `<body><script>
                    globalThis.results = {};
                    const ctx = document.createElement('canvas').getContext('2d');
                    results.canvas = ctx ? 'context' : 'null';
                    const inserted = document.createElement('script');
                    inserted.textContent = 'results.script = "ran"';
                    document.head.appendChild(inserted);
                    const image = new Image();
                    image.onload = () => { results.image = 'load'; };
                    image.onerror = () => { results.image = 'error'; };
                    image.src = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
                <\/script></body>`;
                document.body.appendChild(frame);
                return 1;
            })()
            """);
        string? json = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await page.SettleAsync(50);
            // The port runs a frame in its own realm; read the frame's globals there.
            json = page.Frames.FirstOrDefault(frame => frame.Url == "about:srcdoc")
                ?.Evaluate("JSON.stringify(globalThis.results)")?.GetValue<string>();
            if (json is not null && json.Contains("image", StringComparison.Ordinal))
            {
                break;
            }
        }

        PageFixtures.AssertJson("""{"canvas":"context","script":"ran","image":"load"}""", JsonNode.Parse(json ?? "null"));
    }

    [Fact]
    public async Task SrcdocWinsOverSrcAndRunsWithTheParentsOriginBaseAndCookies()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/dir/page" => TestResponse.Html(
                "<html><head><base href=\"/base/dir/\"></head><body>"
                + "<iframe src=\"/other\" srcdoc=\"<script src='frame.js'></script><script>"
                + "fetch('api'); document.cookie = 'fromframe=1';"
                + " globalThis.seen = location.href + ' ' + document.cookie + ' ' + document.baseURI;"
                + "</script>\"></iframe></body></html>") with
            {
                ExtraHeaders = [("Set-Cookie", "pc=1; Path=/")],
            },
            _ => null,
        });
        using Page page = PageFixtures.NewPage("srcdoc-frame");
        string pageUrl = $"{server.Origin}/dir/page?q=1";
        await page.NavigateAsync(pageUrl);

        TestRequest script = Logged(server, "/base/dir/frame.js");
        Assert.Equal(pageUrl, Header(script, "referer"));
        TestRequest api = Logged(server, "/base/dir/api");
        Assert.Equal(pageUrl, Header(api, "referer"));
        Assert.Equal("pc=1", Header(api, "cookie"));
        Assert.False(Requested(server, "/other"));

        FrameRealm frame = SrcdocFrame(page);
        Assert.Equal(new Uri(server.Origin).GetLeftPart(UriPartial.Authority), frame.Origin);
        Assert.Equal(
            $"about:srcdoc pc=1; fromframe=1 {server.Origin}/base/dir/",
            frame.Evaluate("globalThis.seen")!.GetValue<string>());

        // The frame's cookie is its parent's, with the parent URL's default path.
        Assert.Equal("pc=1; fromframe=1", page.Evaluate("document.cookie")!.GetValue<string>());
        Assert.Equal("/dir", Assert.Single(page.Context.CookieJar.GetAllCookies(), c => c.Name == "fromframe").Path);
        PageFixtures.AssertJson(
            """{"doc":true,"srcdoc":"<script src='frame.js'>"}""",
            page.Evaluate("(() => { const f = document.querySelector('iframe');"
                + " return { doc: f.contentDocument !== null, srcdoc: f.srcdoc.slice(0, 23) }; })()"));
    }

    [Fact]
    public async Task SettingSrcdocNavigatesAndSrcWaitsUntilItIsRemoved()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/page" => TestResponse.Html("<html><body><iframe src=\"/plain\"></iframe></body></html>"),
            "/plain" or "/other" => TestResponse.Html("<p>doc</p>"),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("srcdoc-dynamic");
        await page.NavigateAsync($"{server.Origin}/page");
        Logged(server, "/plain");

        page.Evaluate("(() => { const f = document.querySelector('iframe');"
            + " f.onload = () => { globalThis.loads = (globalThis.loads || 0) + 1; };"
            + " f.srcdoc = '<script>globalThis.v = 1</script>'; return 0; })()");
        await SettleFramesAsync(page);
        Assert.Equal(1, SrcdocFrame(page).Evaluate("globalThis.v")!.GetValue<double>());
        Assert.Equal(1, page.Evaluate("globalThis.loads")!.GetValue<double>());

        page.Evaluate("(document.querySelector('iframe').src = '/other', 0)");
        await SettleFramesAsync(page);
        Assert.False(Requested(server, "/other"));

        page.Evaluate("(document.querySelector('iframe').removeAttribute('srcdoc'), 0)");
        Logged(server, "/other");
    }

    [Fact]
    public async Task SandboxedSrcdocHasAnOpaqueOrigin()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/page" => TestResponse.Html(
                "<html><body><iframe sandbox=\"allow-scripts\" srcdoc=\"<script>"
                + "try { document.cookie } catch (e) { globalThis.read = e.name + ': ' + e.message }"
                + " try { document.cookie = 'x=1' } catch (e) { globalThis.write = e.name + ': ' + e.message }"
                + "</script>\"></iframe><iframe sandbox id=blank></iframe></body></html>") with
            {
                ExtraHeaders = [("Set-Cookie", "pc=1; Path=/")],
            },
            _ => null,
        });
        using Page page = PageFixtures.NewPage("srcdoc-sandboxed");
        await page.NavigateAsync($"{server.Origin}/page");

        FrameRealm frame = SrcdocFrame(page);
        Assert.Equal("null", frame.Origin);
        JsonNode? errors = frame.Evaluate("({ read: globalThis.read, write: globalThis.write })");
        Assert.Equal(
            "SecurityError: Failed to read the 'cookie' property from 'Document': The document is sandboxed and lacks the 'allow-same-origin' flag.",
            errors!["read"]!.GetValue<string>());
        Assert.Equal(
            "SecurityError: Failed to set the 'cookie' property on 'Document': The document is sandboxed and lacks the 'allow-same-origin' flag.",
            errors["write"]!.GetValue<string>());
        PageFixtures.AssertJson(
            """{"srcdoc":true,"blank":true}""",
            page.Evaluate("({ srcdoc: document.querySelector('iframe').contentDocument === null,"
                + " blank: document.getElementById('blank').contentDocument === null })"));
        Assert.Equal("pc=1", page.Evaluate("document.cookie")!.GetValue<string>());
    }
}

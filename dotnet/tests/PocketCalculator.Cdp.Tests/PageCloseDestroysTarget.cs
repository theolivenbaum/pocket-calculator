using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PocketCalculator.Browser;
using PocketCalculator.Js.Ops;
using Xunit;

using BrowserPage = PocketCalculator.Browser.Page;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/page_close_destroys_target.rs</c> as
/// upstream 361f15d extends it (<c>Page.close</c> runs beforeunload, <c>Page.stopLoading</c>
/// cancels pending loads), and of the stop-loading tests 361f15d adds to
/// <c>crates/obscura-cdp/src/server.rs</c>. The plain close and stopLoading acks of
/// upstream e5bcd75 are in <see cref="PageDomainTests"/>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class PageCloseDestroysTarget
{
    private static async Task<CdpResponse> CdpAsync(CdpContext ctx, ulong id, string method, JsonObject parameters, string session) =>
        await Dispatcher.DispatchAsync(
            new CdpRequest { Id = id, Method = method, Params = parameters, SessionId = session },
            ctx);

    private static Task<CdpResponse> EvaluateAsync(CdpContext ctx, ulong id, string expression, string session) =>
        CdpAsync(ctx, id, "Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true }, session);

    private static List<JsonNode> Emitted(CdpContext ctx, string method) =>
        [.. ctx.PendingEvents.Where(e => e.Method == method).Select(e => e.Params!)];

    private static (CdpContext Ctx, string PageId) NewPage(string session)
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[session] = pageId;
        return (ctx, pageId);
    }

    private static double? Number(BrowserPage page, string expression) =>
        page.EvaluateHost(expression)?.GetValue<double>();

    [Fact]
    public async Task PageCloseRunsBeforeunloadAndWaitsForDialogResolution()
    {
        const string session = "close-confirmation";
        (CdpContext ctx, string pageId) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await EvaluateAsync(ctx, 1,
            "globalThis.closeAttempts = 0; addEventListener('beforeunload', e => { closeAttempts++; e.preventDefault(); });", session);
        await CdpAsync(ctx, 6, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = "a", ["code"] = "KeyA" }, session);
        Assert.Null((await CdpAsync(ctx, 2, "Page.close", new JsonObject(), session)).Error);
        BrowserPage page = ctx.GetPage(pageId) ?? throw new InvalidOperationException("a confirmation must not destroy the target");
        Assert.Equal(1.0, Number(ctx.GetSessionPageMut(session)!, "closeAttempts"));
        List<JsonNode> opening = Emitted(ctx, "Page.javascriptDialogOpening");
        Assert.Single(opening);
        Assert.Equal("beforeunload", opening[0]["type"]!.GetValue<string>());
        Assert.Equal(page.UrlString(), opening[0]["url"]!.GetValue<string>());
        Assert.Equal(string.Empty, opening[0]["message"]!.GetValue<string>());
        Assert.True(opening[0]["hasBrowserHandler"]!.GetValue<bool>());
        Assert.Empty(Emitted(ctx, "Target.targetDestroyed"));

        // A second close while the dialog shows does not run the handlers again.
        await CdpAsync(ctx, 7, "Page.close", new JsonObject(), session);
        Assert.Equal(1.0, Number(ctx.GetSessionPageMut(session)!, "closeAttempts"));

        await CdpAsync(ctx, 3, "Page.handleJavaScriptDialog", new JsonObject { ["accept"] = false }, session);
        Assert.NotNull(ctx.GetPage(pageId));
        Assert.False(Emitted(ctx, "Page.javascriptDialogClosed")[0]["result"]!.GetValue<bool>());
        await CdpAsync(ctx, 4, "Page.close", new JsonObject(), session);
        Assert.Equal(2.0, Number(ctx.GetSessionPageMut(session)!, "closeAttempts"));
        await CdpAsync(ctx, 5, "Page.handleJavaScriptDialog", new JsonObject { ["accept"] = true }, session);
        Assert.Null(ctx.GetPage(pageId));
        Assert.Single(Emitted(ctx, "Target.targetDestroyed"));
        Assert.True(Emitted(ctx, "Page.javascriptDialogClosed")[1]["result"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HandleJavaScriptDialogWithoutADialogIsAnError()
    {
        const string session = "no-dialog";
        (CdpContext ctx, _) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        Assert.Equal("No dialog is showing",
            (await CdpAsync(ctx, 1, "Page.handleJavaScriptDialog", new JsonObject { ["accept"] = true }, session)).Error?.Message);
        Assert.Equal("accept required",
            (await CdpAsync(ctx, 2, "Page.handleJavaScriptDialog", new JsonObject(), session)).Error?.Message);
    }

    [Fact]
    public async Task PageCloseDoesNotPromptWithoutUserActivation()
    {
        const string session = "close-no-activation";
        (CdpContext ctx, string pageId) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await EvaluateAsync(ctx, 1, "addEventListener('beforeunload', e => e.preventDefault());", session);
        await CdpAsync(ctx, 2, "Page.close", new JsonObject(), session);
        Assert.Null(ctx.GetPage(pageId));
        Assert.Empty(Emitted(ctx, "Page.javascriptDialogOpening"));
    }

    [Fact]
    public async Task PageCloseHonorsTheBeforeunloadHandlerReturnValue()
    {
        const string session = "close-handler-return";
        (CdpContext ctx, string pageId) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await EvaluateAsync(ctx, 1,
            "onbeforeunload = e => { if (!e.isTrusted || !e.cancelable || !(e instanceof BeforeUnloadEvent)) throw new Error('not native'); return 'Unsaved'; };",
            session);
        await CdpAsync(ctx, 2, "Input.dispatchMouseEvent",
            new JsonObject { ["type"] = "mousePressed", ["x"] = 1, ["y"] = 1, ["button"] = "left" }, session);
        await CdpAsync(ctx, 3, "Page.close", new JsonObject(), session);
        Assert.NotNull(ctx.GetPage(pageId));
        Assert.Single(Emitted(ctx, "Page.javascriptDialogOpening"));
        await CdpAsync(ctx, 4, "Target.closeTarget", new JsonObject { ["targetId"] = pageId }, session);
        Assert.Null(ctx.GetPage(pageId));
    }

    [Fact]
    public async Task PageCloseHonorsAnEmptyBeforeunloadHandlerReturnValue()
    {
        const string session = "close-empty-return";
        (CdpContext ctx, string pageId) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        CdpResponse synthetic = await EvaluateAsync(ctx, 0,
            "globalThis.onbeforeunload = () => false; globalThis.dispatchEvent(new Event('beforeunload', {cancelable: true}))", session);
        Assert.False(synthetic.Result!["result"]!["value"]!.GetValue<bool>());
        await EvaluateAsync(ctx, 1, "onbeforeunload = () => '';", session);
        await CdpAsync(ctx, 2, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = "a", ["code"] = "KeyA" }, session);
        await CdpAsync(ctx, 3, "Page.close", new JsonObject(), session);
        Assert.NotNull(ctx.GetPage(pageId));
        Assert.Single(Emitted(ctx, "Page.javascriptDialogOpening"));
    }

    [Fact]
    public async Task EscapeKeyAndPageScriptDoNotActivateTheDocument()
    {
        const string session = "close-escape";
        (CdpContext ctx, string pageId) = NewPage(session);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await EvaluateAsync(ctx, 1,
            "onbeforeunload = () => 'x'; dispatchEvent(new KeyboardEvent('keydown', {key: 'a'})); dispatchEvent(new MouseEvent('mousedown'));",
            session);
        await CdpAsync(ctx, 2, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = "Escape", ["code"] = "Escape" }, session);
        await CdpAsync(ctx, 3, "Page.close", new JsonObject(), session);
        Assert.Null(ctx.GetPage(pageId));
        Assert.Empty(Emitted(ctx, "Page.javascriptDialogOpening"));
    }

    /// <summary>
    /// A loopback HTTP fixture: optionally answers the first request with
    /// <paramref name="firstBody"/>, then accepts one request it never answers and reports
    /// when the client closes that socket.
    /// </summary>
    private sealed class HeldServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public HeldServer(string? firstBody)
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/";
            Closed = RunAsync(firstBody);
        }

        public string Url { get; }

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Closed { get; }

        private async Task RunAsync(string? firstBody)
        {
            byte[] buffer = new byte[4096];
            if (firstBody is not null)
            {
                using TcpClient first = await _listener.AcceptTcpClientAsync();
                NetworkStream stream = first.GetStream();
                _ = await stream.ReadAsync(buffer);
                byte[] body = Encoding.UTF8.GetBytes(firstBody);
                string head = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: "
                    + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            }

            using TcpClient held = await _listener.AcceptTcpClientAsync();
            NetworkStream heldStream = held.GetStream();
            Assert.True(await heldStream.ReadAsync(buffer) > 0);
            Requested.TrySetResult();
            // No response: cancellation must drop the pending HTTP request.
            while (await heldStream.ReadAsync(buffer) > 0)
            {
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task AssertStopLoadingCancelsNavigation(bool script, string method)
    {
        CoreCdp.AllowLoopback();
        using var server = new HeldServer(
            script ? "<!doctype html><title>Partial</title><script src='/held.js'></script>" : null);
        BrowserContext context = BrowserContext.WithStorageAndNetwork("stop-loading-test", null, false, null, null, true);
        var ctx = CdpContext.NewWithSharedContext(context);
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        string otherPage = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;
        ctx.Sessions["raw-session"] = pageId;
        ctx.Sessions["other-session"] = otherPage;
        string originalUrl = ctx.GetPage(pageId)!.UrlString();

        var reply = Channel.CreateUnbounded<string>();
        var rx = Channel.CreateUnbounded<ServerMessage>();
        var interceptRx = Channel.CreateUnbounded<InterceptedRequest>();
        var deferred = new Queue<ServerMessage>();
        string command = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Page.navigate",
            ["sessionId"] = session,
            ["params"] = new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
        }.ToJsonString();
        Task navigation = CdpServer.ProcessWithInterceptionAsync(
            command, ctx, reply.Writer, rx.Reader, interceptRx.Reader, [], deferred, true);
        await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        foreach ((int id, string sessionId) in new[] { (2, "other-session"), (3, "raw-session") })
        {
            string text = new JsonObject
            {
                ["id"] = id,
                ["method"] = id == 2 ? "Page.stopLoading" : method,
                ["sessionId"] = sessionId,
                ["params"] = new JsonObject { ["targetId"] = pageId },
            }.ToJsonString();
            rx.Writer.TryWrite(new ServerMessage.Cdp(text, reply.Writer));
        }

        await navigation.WaitAsync(TimeSpan.FromSeconds(5));
        await server.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        // Another target's stop is deferred, not answered here, and cancels nothing.
        Assert.Equal(method == "Page.stopLoading" ? 1 : 2, deferred.Count);
        while (deferred.TryDequeue(out ServerMessage? message))
        {
            if (message is ServerMessage.Cdp cdp)
            {
                await CdpServer.ProcessCdpMessageAsync(cdp.Text, ctx, cdp.ReplyTx);
            }
        }

        List<JsonNode> replies = [];
        while (reply.Reader.TryRead(out string? text))
        {
            if (JsonNode.Parse(text) is { } node)
            {
                replies.Add(node);
            }
        }

        bool IsId(JsonNode node, int id) => node["id"] is JsonValue value && value.GetValue<long>() == id;
        Assert.Single(replies, r => IsId(r, 3));
        JsonNode stopped = replies.First(r => IsId(r, 3));
        Assert.Null(stopped["error"]);
        JsonNode aborted = replies.First(r => IsId(r, 1));
        Assert.Null(aborted["error"]);
        if (!script)
        {
            Assert.Equal("net::ERR_ABORTED", aborted["result"]!["errorText"]!.GetValue<string>());
            JsonNode failed = replies.First(r => r["method"]?.GetValue<string>() == "Network.loadingFailed");
            Assert.True(failed["params"]!["canceled"]!.GetValue<bool>());
        }
        else
        {
            Assert.Null(aborted["result"]!["errorText"]);
        }

        Assert.DoesNotContain(replies, r => r["method"]?.GetValue<string>() == "Page.loadEventFired");
        Assert.Contains(replies, r => r["method"]?.GetValue<string>() == "Page.frameStoppedLoading");
        Assert.NotNull(ctx.GetPage(otherPage));
        if (method != "Page.stopLoading")
        {
            Assert.Null(ctx.GetPage(pageId));
            Assert.Single(replies, r => r["method"]?.GetValue<string>() == "Target.targetDestroyed");
            return;
        }

        Assert.Equal(script ? server.Url : originalUrl, ctx.GetPage(pageId)!.UrlString());
        Assert.Equal(3.0, Number(ctx.GetSessionPageMut(session)!, "1 + 2"));
        await CdpServer.ProcessCdpMessageAsync(
            new JsonObject
            {
                ["id"] = 4,
                ["method"] = "Page.navigate",
                ["sessionId"] = session,
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            }.ToJsonString(),
            ctx,
            reply.Writer);
        Assert.Equal("about:blank", ctx.GetPage(pageId)!.UrlString());
    }

    [Fact]
    public Task StopLoadingCancelsAHeldDocumentRequest() => AssertStopLoadingCancelsNavigation(false, "Page.stopLoading");

    [Fact]
    public Task StopLoadingCancelsAHeldParserScriptRequest() => AssertStopLoadingCancelsNavigation(true, "Page.stopLoading");

    [Fact]
    public Task StopLoadingCancelsForPageClose() => AssertStopLoadingCancelsNavigation(false, "Page.close");

    [Fact]
    public Task StopLoadingCancelsForTargetClose() => AssertStopLoadingCancelsNavigation(false, "Target.closeTarget");

    private static async Task AssertStopLoadingCancelsImages(bool native)
    {
        CoreCdp.AllowLoopback();
        using var server = new HeldServer(native ? "<!doctype html><title>Images</title>" : "<!doctype html><img src='/held.png'>");
        BrowserContext context = BrowserContext.WithStorageAndNetwork("stop-resources-test", null, false, null, null, true);
        var ctx = CdpContext.NewWithSharedContext(context);
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string session = "stop-resources";
        ctx.Sessions[session] = pageId;
        CdpResponse navigated = await CdpAsync(ctx, 1, "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "domcontentloaded" }, session);
        Assert.Null(navigated.Error);
        BrowserPage page = ctx.GetSessionPageMut(session)!;
        if (native)
        {
            await EvaluateAsync(ctx, 10,
                "globalThis.imageErrors = 0; globalThis.images = [new Image(), new Image()]; for (const image of images) { image.src = '/held.png'; image.decode().catch(() => imageErrors++); }",
                session);
            await page.Js!.RunEventLoopBoundedAsync(10);
        }

        page.QueuePendingRenderResources();
        await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await CdpAsync(ctx, 2, "Page.stopLoading", new JsonObject(), session)).Error);
        page = ctx.GetSessionPageMut(session)!;
        await page.Js!.RunEventLoopBoundedAsync(50);
        if (native)
        {
            Assert.Equal(2.0, Number(page, "imageErrors"));
        }

        Assert.False(page.HasPendingRenderResources, "stop must cancel pending image loads");
        page.QueuePendingRenderResources();
        Assert.False(page.HasPendingRenderResources, "the autonomous pump must not restart cancelled loads");
        Assert.True(page.Js!.RenderResourceIsKnown(server.Url + "held.png"));
        await server.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(2100);
        page.QueuePendingRenderResources();
        Assert.False(page.HasPendingRenderResources, "stopped loads must not restart after the failure retry window");
    }

    [Fact]
    public Task StopLoadingCancelsBackgroundResourcesWithoutRestartingThem() => AssertStopLoadingCancelsImages(false);

    [Fact]
    public Task StopLoadingCancelsNativeImageRequestsAndDecodeWaiters() => AssertStopLoadingCancelsImages(true);
}

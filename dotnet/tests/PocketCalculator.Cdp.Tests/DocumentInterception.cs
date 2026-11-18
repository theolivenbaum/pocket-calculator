using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PocketCalculator.Browser;
using PocketCalculator.Js.Ops;
using Xunit;

using BrowserPage = PocketCalculator.Browser.Page;
using PageDomain = PocketCalculator.Cdp.Domains.Page;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// upstream b76d966: Fetch interception pauses the main-document request before transport.
/// Ports of the <c>routed_document_*</c>, <c>intercepted_documents_keep_their_own_loader_and_page_identity</c>
/// and <c>fulfill_request_preserves_repeated_set_cookie_headers</c> tests in <c>server.rs</c>, and
/// <c>same_url_frame_response_cannot_replace_the_main_document_loader</c> in <c>domains/page.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class DocumentInterception
{
    private static string ClosedPortUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/start";
    }

    private static async Task<List<JsonNode>> RoutedDocumentEventsAsync(string scenario)
    {
        string url = ClosedPortUrl();
        var context = BrowserContext.WithStorageAndNetwork("routed-lifecycle", null, false, null, null, true);
        CdpContext ctx = CdpContext.NewWithSharedContext(context);
        string pageId = ctx.CreatePage();
        string session = pageId + "-session";
        ctx.Sessions[session] = pageId;
        ChannelReader<InterceptedRequest> interceptRx = ctx.GetPage(pageId)!.EnableInterception();
        var server = Channel.CreateUnbounded<ServerMessage>();
        var reply = Channel.CreateUnbounded<string>();
        string command = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Page.navigate",
            ["sessionId"] = session,
            ["params"] = new JsonObject { ["url"] = url },
        }.ToJsonString();
        Dictionary<string, PausedIntercept> paused = new(StringComparer.Ordinal);

        Task navigation = CdpServer.ProcessWithInterceptionAsync(
            command, ctx, reply.Writer, server.Reader, interceptRx, paused, new Queue<ServerMessage>(), true);
        List<JsonNode> events = [];
        int pauses = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            Task<bool> next = reply.Reader.WaitToReadAsync(timeout.Token).AsTask();
            await Task.WhenAny(navigation, next);
            while (reply.Reader.TryRead(out string? text))
            {
                JsonNode value = JsonNode.Parse(text)!;
                if (value["method"]?.GetValue<string>() == "Fetch.requestPaused")
                {
                    pauses++;
                    string method = scenario == "abort" ? "Fetch.failRequest"
                        : scenario == "transport" || (scenario == "chain-fail" && pauses == 2) ? "Fetch.continueRequest"
                        : "Fetch.fulfillRequest";
                    string body = pauses == 1 && scenario.StartsWith("chain", StringComparison.Ordinal)
                        ? "<script>location.replace('/finish')</script>"
                        : pauses == 1 && scenario == "repeat"
                            ? "<script>location.reload()</script>"
                            : "<title>Finished</title>";
                    server.Writer.TryWrite(new ServerMessage.Cdp(
                        new JsonObject
                        {
                            ["id"] = pauses + 1,
                            ["method"] = method,
                            ["sessionId"] = session,
                            ["params"] = new JsonObject
                            {
                                ["requestId"] = value["params"]!["requestId"]!.GetValue<string>(),
                                ["errorReason"] = "Aborted",
                                ["responseCode"] = 200,
                                ["responseHeaders"] = new JsonArray
                                {
                                    new JsonObject { ["name"] = "Content-Type", ["value"] = "text/html" },
                                },
                                ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
                            },
                        }.ToJsonString(),
                        reply.Writer));
                }

                events.Add(value);
            }

            if (navigation.IsCompleted)
            {
                await navigation;
                break;
            }
        }

        foreach (BrowserPage page in ctx.Pages)
        {
            page.Dispose();
        }

        return events;
    }

    private static void AssertRoutedCompletions(List<JsonNode> events, int finished, int failed)
    {
        List<JsonNode> Named(string method) =>
            events.Where(e => e["method"]?.GetValue<string>() == method).ToList();
        var requests = Named("Network.requestWillBeSent");
        var completed = Named("Network.loadingFinished");
        var errors = Named("Network.loadingFailed");
        string all = string.Join('\n', events.Select(e => e.ToJsonString()));
        Assert.True(requests.Count == finished + failed, all);
        Assert.True(completed.Count == finished, all);
        Assert.True(errors.Count == failed, all);
        foreach (JsonNode request in requests)
        {
            string id = request["params"]!["requestId"]!.GetValue<string>();
            Assert.True(
                completed.Concat(errors).Count(e => e["params"]!["requestId"]!.GetValue<string>() == id) == 1,
                "each published request must finish exactly once: " + all);
        }
    }

    [Fact]
    public async Task RoutedDocumentTransportFailureFinishesItsRequest() =>
        AssertRoutedCompletions(await RoutedDocumentEventsAsync("transport"), 0, 1);

    [Fact]
    public async Task RoutedDocumentAbortFinishesOnceWithCanonicalError()
    {
        var events = await RoutedDocumentEventsAsync("abort");
        AssertRoutedCompletions(events, 0, 1);
        JsonNode error = events.First(e => e["method"]?.GetValue<string>() == "Network.loadingFailed");
        Assert.Equal("net::ERR_ABORTED", error["params"]!["errorText"]!.GetValue<string>());
        Assert.True(error["params"]!["canceled"]!.GetValue<bool>());
        Assert.Equal("Document", error["params"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoutedDocumentFulfillmentIsTheNavigationsLoader()
    {
        var events = await RoutedDocumentEventsAsync("fulfill");
        AssertRoutedCompletions(events, 1, 0);
        JsonNode request = events.First(e => e["method"]?.GetValue<string>() == "Network.requestWillBeSent");
        string requestId = request["params"]!["requestId"]!.GetValue<string>();
        Assert.Equal(requestId, request["params"]!["loaderId"]!.GetValue<string>());
        Assert.EndsWith("/start", request["params"]!["documentURL"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonNode response = events.First(e => e["id"]?.GetValue<int>() == 1);
        Assert.Equal(requestId, response["result"]!["loaderId"]!.GetValue<string>());
        JsonNode navigated = events.First(e => e["method"]?.GetValue<string>() == "Page.frameNavigated");
        Assert.Equal(requestId, navigated["params"]!["frame"]!["loaderId"]!.GetValue<string>());
        JsonNode received = events.First(e => e["method"]?.GetValue<string>() == "Network.responseReceived");
        Assert.Equal(requestId, received["params"]!["requestId"]!.GetValue<string>());
        Assert.Equal(200, received["params"]!["response"]!["status"]!.GetValue<int>());
        // Paused for real, so never again as an after-the-fact notice.
        Assert.Single(events, e => e["method"]?.GetValue<string>() == "Fetch.requestPaused");
    }

    [Fact]
    public async Task RoutedDocumentScriptNavigationPreservesBothCompletions() =>
        AssertRoutedCompletions(await RoutedDocumentEventsAsync("chain"), 2, 0);

    [Fact]
    public async Task RoutedDocumentReloadPreservesBothCompletions() =>
        AssertRoutedCompletions(await RoutedDocumentEventsAsync("repeat"), 2, 0);

    [Fact]
    public async Task RoutedDocumentScriptNavigationFailureFinishesBothRequests() =>
        AssertRoutedCompletions(await RoutedDocumentEventsAsync("chain-fail"), 1, 1);

    /// <summary>
    /// Through the connection processor: Fetch.enable pauses a client's Page.navigate, and a
    /// navigation a script queues afterwards (location.href from Runtime.evaluate) pauses too,
    /// rather than hanging inside an inline dispatch nobody can answer the pause from.
    /// </summary>
    [Fact]
    public async Task FetchEnablePausesClientAndScriptNavigations()
    {
        var server = Channel.CreateUnbounded<ServerMessage>();
        var replies = Channel.CreateUnbounded<string>();
        using var shutdown = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        Task processor = CdpServer.CdpProcessorAsync(
            server.Reader, CdpContext.New().DefaultContext, shutdown.Token, stop.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        async Task<JsonNode> NextAsync() =>
            JsonNode.Parse(await replies.Reader.ReadAsync(deadline.Token))!;

        void Send(JsonObject value) =>
            server.Writer.TryWrite(new ServerMessage.Cdp(value.ToJsonString(), replies.Writer));

        async Task<List<JsonNode>> UntilAsync(Func<JsonNode, bool> done)
        {
            List<JsonNode> seen = [];
            while (true)
            {
                JsonNode value = await NextAsync();
                seen.Add(value);
                if (value["method"]?.GetValue<string>() == "Fetch.requestPaused")
                {
                    string requestId = value["params"]!["requestId"]!.GetValue<string>();
                    string url = value["params"]!["request"]!["url"]!.GetValue<string>();
                    string html = url.EndsWith("/next", StringComparison.Ordinal)
                        ? "<title>Second</title>"
                        : "<title>First</title>";
                    Send(new JsonObject
                    {
                        ["id"] = 900,
                        ["method"] = "Fetch.fulfillRequest",
                        ["sessionId"] = value["sessionId"]!.GetValue<string>(),
                        ["params"] = new JsonObject
                        {
                            ["requestId"] = requestId,
                            ["responseCode"] = 200,
                            ["responseHeaders"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "Content-Type", ["value"] = "text/html" },
                            },
                            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)),
                        },
                    });
                }

                if (done(value))
                {
                    return seen;
                }
            }
        }

        try
        {
            server.Writer.TryWrite(new ServerMessage.NewConnection(replies.Writer));
            await NextAsync();
            Send(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            });
            string? session = null;
            await UntilAsync(value =>
            {
                session ??= value["params"]?["sessionId"]?.GetValue<string>();
                return value["id"]?.GetValue<int>() == 1;
            });
            Assert.NotNull(session);

            Send(new JsonObject
            {
                ["id"] = 2,
                ["method"] = "Fetch.enable",
                ["sessionId"] = session,
                ["params"] = new JsonObject
                {
                    ["patterns"] = new JsonArray { new JsonObject { ["urlPattern"] = "*example.test*" } },
                },
            });
            await UntilAsync(value => value["id"]?.GetValue<int>() == 2);

            Send(new JsonObject
            {
                ["id"] = 3,
                ["method"] = "Page.navigate",
                ["sessionId"] = session,
                ["params"] = new JsonObject { ["url"] = "http://example.test/start" },
            });
            var first = await UntilAsync(value => value["method"]?.GetValue<string>() == "Page.frameNavigated");
            Assert.Contains(first, value => value["method"]?.GetValue<string>() == "Fetch.requestPaused");
            JsonNode response = first.First(value => value["id"]?.GetValue<int>() == 3);
            Assert.Null(response["error"]);

            Send(new JsonObject
            {
                ["id"] = 4,
                ["method"] = "Runtime.evaluate",
                ["sessionId"] = session,
                ["params"] = new JsonObject { ["expression"] = "location.href = '/next'; 1" },
            });
            var second = await UntilAsync(value => value["method"]?.GetValue<string>() == "Page.frameNavigated");
            JsonNode paused = second.First(value => value["method"]?.GetValue<string>() == "Fetch.requestPaused");
            Assert.Equal("http://example.test/next", paused["params"]!["request"]!["url"]!.GetValue<string>());
            Assert.Equal(
                "http://example.test/next",
                second.Last()["params"]!["frame"]!["url"]!.GetValue<string>());
        }
        finally
        {
            server.Writer.TryComplete();
            await stop.CancelAsync();
            await processor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// upstream 5e58c86 <c>fetch_resolution_is_handled_once_by_the_outer_processor</c>, second
    /// half: failing a script request finishes it with one <c>Network.loadingFailed</c> before
    /// the command's response; failing a main document leaves that to the navigation.
    /// </summary>
    [Fact]
    public void FailRequestFinishesAScriptRequestOnce()
    {
        var resolution = new TaskCompletionSource<InterceptResolution>();
        var document = new TaskCompletionSource<InterceptResolution>();
        Dictionary<string, PausedIntercept> paused = new(StringComparer.Ordinal)
        {
            ["request-2"] = new(resolution),
            ["page-1.1"] = new(document, true),
        };
        var reply = Channel.CreateUnbounded<string>();
        Assert.True(ServerSupport.HandleFetchResolution(
            """{"id":18,"method":"Fetch.failRequest","sessionId":"page-session","params":{"requestId":"request-2","errorReason":"Aborted"}}""",
            CdpContext.New(), reply.Writer, paused));
        Assert.Equal("Aborted", Assert.IsType<InterceptResolution.Fail>(resolution.Task.Result).Reason);
        Assert.True(reply.Reader.TryRead(out string? eventText));
        JsonNode failed = JsonNode.Parse(eventText)!;
        Assert.Equal("Network.loadingFailed", failed["method"]!.GetValue<string>());
        Assert.Equal("page-session", failed["sessionId"]!.GetValue<string>());
        Assert.Equal("request-2", failed["params"]!["requestId"]!.GetValue<string>());
        Assert.Equal("Fetch", failed["params"]!["type"]!.GetValue<string>());
        Assert.Equal("net::ERR_ABORTED", failed["params"]!["errorText"]!.GetValue<string>());
        Assert.True(failed["params"]!["canceled"]!.GetValue<bool>());
        Assert.True(reply.Reader.TryRead(out string? responseText));
        Assert.Equal(18, JsonNode.Parse(responseText)!["id"]!.GetValue<int>());
        Assert.False(reply.Reader.TryRead(out _), "abort emits one failure and one response");

        Assert.True(ServerSupport.HandleFetchResolution(
            """{"id":19,"method":"Fetch.failRequest","sessionId":"page-session","params":{"requestId":"page-1.1","errorReason":"ConnectionRefused"}}""",
            CdpContext.New(), reply.Writer, paused));
        Assert.IsType<InterceptResolution.Fail>(document.Task.Result);
        Assert.True(reply.Reader.TryRead(out responseText));
        Assert.Equal(19, JsonNode.Parse(responseText)!["id"]!.GetValue<int>());
        Assert.False(reply.Reader.TryRead(out _), "a main document's failure is the navigation's to report");
    }

    /// <summary>
    /// upstream 5e58c86 <c>awaited_runtime_requests_keep_servicing_route_replies</c>: an awaited
    /// evaluation whose fetches pause still gets their resolutions, and each fulfilled request
    /// finishes. Upstream also asserts an unrelated evaluate sent meanwhile is answered only after
    /// the awaited one; the port parks an awaited command and serves others meanwhile, as
    /// Chromium does (ParkedCommand), so that ordering is not asserted.
    /// </summary>
    [Fact]
    public async Task AwaitedRuntimeRequestsKeepServicingRouteReplies()
    {
        var server = Channel.CreateUnbounded<ServerMessage>();
        var replies = Channel.CreateUnbounded<string>();
        using var shutdown = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        Task processor = CdpServer.CdpProcessorAsync(
            server.Reader, CdpContext.New().DefaultContext, shutdown.Token, stop.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        async Task<JsonNode> NextAsync() =>
            JsonNode.Parse(await replies.Reader.ReadAsync(deadline.Token))!;

        void Send(JsonObject value) =>
            server.Writer.TryWrite(new ServerMessage.Cdp(value.ToJsonString(), replies.Writer));

        try
        {
            server.Writer.TryWrite(new ServerMessage.NewConnection(replies.Writer));
            await NextAsync();
            Send(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            });
            string? session = null;
            while (true)
            {
                JsonNode value = await NextAsync();
                session ??= value["params"]?["sessionId"]?.GetValue<string>();
                if (value["id"]?.GetValue<int>() == 1)
                {
                    break;
                }
            }

            Assert.NotNull(session);
            Send(new JsonObject
            {
                ["id"] = 2,
                ["method"] = "Fetch.enable",
                ["sessionId"] = session,
                ["params"] = new JsonObject
                {
                    ["patterns"] = new JsonArray { new JsonObject { ["urlPattern"] = "http://example.test/*" } },
                },
            });
            while ((await NextAsync())["id"]?.GetValue<int>() != 2)
            {
            }

            const string Body =
                "let result=''; for(let i=0;i<2;i++) result+=await (await fetch('http://example.test/'+i)).text(); return result";
            foreach (var (id, method, parameters) in new[]
            {
                (3, "Runtime.evaluate", new JsonObject
                {
                    ["expression"] = "(async()=>{" + Body + "})()",
                    ["awaitPromise"] = true,
                    ["returnByValue"] = true,
                }),
                (4, "Runtime.callFunctionOn", new JsonObject
                {
                    ["functionDeclaration"] = "async function(){" + Body + "}",
                    ["awaitPromise"] = true,
                    ["returnByValue"] = true,
                }),
            })
            {
                Send(new JsonObject { ["id"] = id, ["method"] = method, ["sessionId"] = session, ["params"] = parameters });
                Send(new JsonObject
                {
                    ["id"] = 10 + id,
                    ["method"] = "Runtime.evaluate",
                    ["sessionId"] = session,
                    ["params"] = new JsonObject { ["expression"] = "21*2", ["returnByValue"] = true },
                });
                int pauses = 0;
                int finished = 0;
                bool completed = false;
                bool otherCompleted = false;
                HashSet<string> pausedIds = [];
                while (!(completed && otherCompleted && finished == 2))
                {
                    JsonNode value = await NextAsync();
                    string? eventName = value["method"]?.GetValue<string>();
                    if (eventName == "Fetch.requestPaused")
                    {
                        Assert.Equal(session, value["sessionId"]!.GetValue<string>());
                        pauses++;
                        string requestId = value["params"]!["requestId"]!.GetValue<string>();
                        pausedIds.Add(requestId);
                        Send(new JsonObject
                        {
                            ["id"] = 100 + pauses,
                            ["method"] = "Fetch.fulfillRequest",
                            ["sessionId"] = session,
                            ["params"] = new JsonObject
                            {
                                ["requestId"] = requestId,
                                ["responseCode"] = 200,
                                ["body"] = "b2s=",
                                ["responseHeaders"] = new JsonArray
                                {
                                    new JsonObject { ["name"] = "access-control-allow-origin", ["value"] = "*" },
                                },
                            },
                        });
                    }

                    if (eventName == "Network.loadingFinished"
                        && pausedIds.Contains(value["params"]!["requestId"]!.GetValue<string>()))
                    {
                        finished++;
                    }

                    // A paused request is announced once, when it pauses.
                    if (eventName == "Network.requestWillBeSent")
                    {
                        Assert.DoesNotContain(value["params"]!["requestId"]!.GetValue<string>(), pausedIds);
                    }

                    if (value["id"]?.GetValue<int>() == id)
                    {
                        Assert.True(value["error"] is null, value.ToJsonString());
                        Assert.Equal("okok", value["result"]!["result"]!["value"]!.GetValue<string>());
                        Assert.Equal(2, pauses);
                        completed = true;
                    }

                    if (value["id"]?.GetValue<int>() == 10 + id)
                    {
                        Assert.Equal(42.0, value["result"]!["result"]!["value"]!.GetValue<double>());
                        otherCompleted = true;
                    }
                }
            }
        }
        finally
        {
            server.Writer.TryComplete();
            await stop.CancelAsync();
            await processor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void InterceptedDocumentsKeepTheirOwnLoaderAndPageIdentity()
    {
        CdpContext ctx = CdpContext.New();
        string navigating = ctx.CreatePage();
        string other = ctx.CreatePage();
        var reply = Channel.CreateUnbounded<string>();
        Dictionary<string, PausedIntercept> paused = new(StringComparer.Ordinal);
        string navigationId = navigating + ".1";
        string otherId = other + ".1";
        foreach (var (page, requestId, expectedLoader) in new[]
        {
            (navigating, navigationId, navigationId),
            (other, otherId, string.Empty),
            (navigating, "fetch-iframe", string.Empty),
        })
        {
            var intercepted = new InterceptedRequest
            {
                RequestId = requestId,
                PageId = page,
                Url = "http://example.test/",
                Method = "GET",
                Headers = new Dictionary<string, string>(),
                ResourceType = "Document",
            };
            string? loader = ServerSupport.IsMainDocumentIntercept(intercepted, navigating) ? requestId : null;
            ServerSupport.EmitInterceptedRequest(intercepted, "frame-nav", "driver", reply.Writer, paused, loader);
            Assert.True(reply.Reader.TryRead(out string? requestText));
            Assert.True(reply.Reader.TryRead(out string? pauseText));
            JsonNode request = JsonNode.Parse(requestText)!;
            JsonNode pause = JsonNode.Parse(pauseText)!;
            Assert.Equal(requestId, request["params"]!["requestId"]!.GetValue<string>());
            Assert.Equal(expectedLoader, request["params"]!["loaderId"]!.GetValue<string>());
            Assert.Equal(requestId, pause["params"]!["networkId"]!.GetValue<string>());
            Assert.Equal(expectedLoader.Length != 0, paused[requestId].MainDocument);
        }

        Assert.Equal(3, paused.Count);
    }

    [Fact]
    public void FulfillRequestPreservesRepeatedSetCookieHeaders()
    {
        var resolution = new TaskCompletionSource<InterceptResolution>();
        Dictionary<string, PausedIntercept> paused =
            new(StringComparer.Ordinal) { ["document-1"] = new(resolution, true) };
        var reply = Channel.CreateUnbounded<string>();
        Assert.True(ServerSupport.HandleFetchResolution(
            """{"id":1,"method":"Fetch.fulfillRequest","params":{"requestId":"document-1","responseCode":200,"responseHeaders":[{"name":"Set-Cookie","value":"first=1; Path=/"},{"name":"Set-Cookie","value":"second=2; Path=/"},{"name":"set-cookie","value":"third=3; Path=/"},{"name":"Content-Type","value":"text/html"}],"body":"AP+A"}}""",
            CdpContext.New(), reply.Writer, paused));
        var fulfill = Assert.IsType<InterceptResolution.Fulfill>(resolution.Task.Result);
        Assert.Equal("first=1; Path=/\nsecond=2; Path=/\nthird=3; Path=/", fulfill.Headers["set-cookie"]);
        Assert.Equal("text/html", fulfill.Headers["Content-Type"]);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x80 }, fulfill.BodyBytes);
    }

    [Fact]
    public void SameUrlFrameResponseCannotReplaceTheMainDocumentLoader()
    {
        CdpContext ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = pageId + "-session";
        ctx.Sessions[session] = pageId;
        const string url = "https://example.test/";
        var document = new NetworkEvent
        {
            RequestId = pageId + ".1",
            Intercepted = true,
            IsNavigation = true,
            Url = url,
            Method = "GET",
            ResourceType = "Document",
            Status = 200,
            Headers = new Dictionary<string, string>(),
            ResponseHeaders = new Dictionary<string, string> { ["content-type"] = "text/html" },
            BodySize = 0,
            Timestamp = 42.0,
        };
        var frame = new NetworkEvent
        {
            RequestId = "fetch-1",
            Intercepted = true,
            Url = url,
            Method = "GET",
            ResourceType = "Document",
            Status = 200,
            Headers = new Dictionary<string, string>(),
            ResponseHeaders = new Dictionary<string, string> { ["content-type"] = "image/png" },
            BodySize = 0,
            Timestamp = 42.0,
        };
        PageDomain.EmitNavigationEvents(
            ctx, session, pageId, document.RequestId, url, pageId, [document, frame], WaitUntil.Load, true);
        CdpEvent committed = ctx.PendingEvents.First(e => e.Method == "Page.frameNavigated");
        Assert.Equal("text/html", committed.Params!["frame"]!["mimeType"]!.GetValue<string>());
        var responses = ctx.PendingEvents.Where(e => e.Method == "Network.responseReceived").ToList();
        Assert.Equal(2, responses.Count);
        Assert.Equal(document.RequestId, responses[0].Params!["requestId"]!.GetValue<string>());
        Assert.Equal("fetch-1", responses[1].Params!["requestId"]!.GetValue<string>());
        Assert.DoesNotContain(ctx.PendingEvents, e => e.Method == "Network.requestWillBeSent");
    }
}

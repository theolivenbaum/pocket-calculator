using System.Net;
using System.Net.Sockets;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 0155807: fetch() and Response bodies follow an AbortSignal. The expected values
/// were measured in Chromium (playwright chromium, an in-page fetch against a local server).
/// </summary>
public sealed partial class RuntimeTests
{
    private const string AbortOkResponse =
        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\nok";

    private static RuntimeFixture LoopbackPage(string origin)
    {
        var fixture = RuntimeFixture.Blank();
        fixture.Runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        fixture.Runtime.SetUrl($"{origin}/page");
        fixture.Runtime.SetHttpClient(new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        fixture.Runtime.RunPageInit();
        return fixture;
    }

    [Fact]
    public async Task FetchAbortPreventsPreAbortedRequestsFromReachingTransport()
    {
        using var server = new RawHttpServer(_ => AbortOkResponse);
        using var fixture = LoopbackPage(server.Origin);
        var target = server.Origin + "/resource";
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const reason = { why: 'cancelled' };
                const controller = new AbortController(); controller.abort(reason);
                const inherited = new Request('{{target}}', { signal: controller.signal });
                const results = [];
                for (const [input, init] of [['{{target}}', {signal:controller.signal}], [inherited, {}]]) {
                    results.push(await fetch(input, init).then(() => false, error => error === reason));
                }
                // An explicit null overrides an inherited aborted signal.
                results.push(await fetch(inherited, {signal:null}).then(r => r.text()) === 'ok');
                return results;
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("[true, true, true]", result.Value);
        Assert.Single(server.RequestLines);
        Assert.Equal(0, fixture.Runtime.State.PageInFlight.Value);
    }

    [Fact]
    public async Task FetchAbortAcceptsCrossRealmSignalsButRejectsForgedPrototypes()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const frame = document.createElement('iframe'); document.body.appendChild(frame);
                const controller = new frame.contentWindow.AbortController();
                const reason = { cancelled: true }; controller.abort(reason);
                const outcomes = [];
                outcomes.push(await fetch('https://example.com/', {signal:controller.signal})
                    .then(() => false, error => error === reason));
                try {
                    const request = new Request('https://example.com/', {signal:controller.signal});
                    outcomes.push(await fetch(request).then(() => false, error => error === reason));
                } catch (_) { outcomes.push(false); }
                const forged = Object.create(AbortSignal.prototype);
                outcomes.push(await fetch('https://example.com/', {signal:forged})
                    .then(() => false, error => error instanceof TypeError));
                try { new Request('https://example.com/', {signal:forged}); outcomes.push(false); }
                catch (error) { outcomes.push(error instanceof TypeError); }
                return outcomes;
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("[true, true, true, true]", result.Value);
        Assert.Equal(0, fixture.Runtime.State.PageInFlight.Value);
    }

    [Fact]
    public async Task FetchAbortRejectsNonSignalsWithChromiumsMessages()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const out = [await fetch('https://example.com/', {signal:{}}).then(() => '', e => e.message)];
                try { new Request('https://example.com/', {signal:{}}); out.push(''); } catch (e) { out.push(e.message); }
                const request = new Request('https://example.com/');
                out.push(request.signal instanceof AbortSignal, request.signal.aborted);
                return out;
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals(
            """
            [
                "Failed to execute 'fetch' on 'Window': Failed to read the 'signal' property from 'RequestInit': Failed to convert value to 'AbortSignal'.",
                "Failed to construct 'Request': Failed to read the 'signal' property from 'RequestInit': Failed to convert value to 'AbortSignal'.",
                true,
                false
            ]
            """,
            result.Value);
    }

    [Fact]
    public async Task FetchAbortReadsSignalGettersOnce()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const reason = { cancelled: true };
                const signal = AbortSignal.abort(reason);
                let fetchReads = 0, requestReads = 0;
                const rejected = await fetch('https://example.com/', {
                    get signal() { fetchReads++; return signal; }
                }).then(() => false, error => error === reason);
                const request = new Request('https://example.com/', {
                    get signal() { requestReads++; return signal; }
                });
                return [rejected, request.signal.aborted, fetchReads, requestReads];
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("[true, true, 1, 1]", result.Value);
    }

    [Fact]
    public void RequestSignalsFollowSourcesWithoutAliasingOrReorderingAbortEvents()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const controller = new AbortController();
                const request = new Request('https://example.com/', {signal:controller.signal});
                const clone = request.clone();
                const nested = AbortSignal.any([request.signal,clone.signal]);
                const reason = {cancelled:true};
                const order = [];
                controller.signal.addEventListener('abort', () => {
                    order.push(['parent',request.signal.aborted,clone.signal.aborted,nested.aborted]);
                });
                request.signal.addEventListener('abort',()=>order.push(['request']));
                clone.signal.addEventListener('abort',()=>order.push(['clone']));
                nested.addEventListener('abort',()=>order.push(['nested']));
                const distinct = request.signal !== controller.signal && clone.signal !== request.signal;
                controller.abort(reason);
                return {distinct,order,reasons:request.signal.reason===reason &&
                    clone.signal.reason===reason && nested.reason===reason};
            })()
            """);
        AssertJsonEquals(
            """
            {
                "distinct": true,
                "order": [["parent",true,true,true],["request"],["clone"],["nested"]],
                "reasons": true
            }
            """,
            result);
    }

    [Fact]
    public async Task FetchAbortErrorsCompletedUnreadResponseStreams()
    {
        using var server = new RawHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\ntail");
        using var fixture = LoopbackPage(server.Origin);
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "completed-abort-fetch",
            $$"""
            globalThis.completedController = new AbortController();
            globalThis.completedReader = null;
            globalThis.completedResponse = null;
            fetch('{{server.Origin}}/resource', {signal:completedController.signal}).then(response => {
                completedResponse = response.clone();
                completedReader = response.body.getReader();
                globalThis.completedClosed = completedReader.closed.then(
                    () => false, error => error.name === 'AbortError');
            });
            """);
        Assert.True(await EventLoopWait.UntilAsync(rt, "completedReader !== null"));
        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                completedController.abort();
                return [await completedReader.read().then(
                    () => false, error => error.name === 'AbortError'), await completedClosed,
                    // A body read after the abort rejects and stays unused, as in Chromium.
                    await completedResponse.text().then(() => false, error => error.name === 'AbortError'),
                    completedResponse.bodyUsed];
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("[true, true, true, false]", result.Value);
        Assert.Equal(0, rt.State.PageInFlight.Value);
    }

    [Fact]
    public void RequestFollowingSignalsAreCollectibleWithoutObservers()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "signal-lifetime",
            """
            globalThis.lifetimeController = new AbortController();
            globalThis.unobservedSignals = Array.from({length:64}, () => new WeakRef(
                new Request('https://example.com/', {signal:lifetimeController.signal}).signal));
            globalThis.lifetimeEvents = [];
            (() => {
                const signal = new Request('https://example.com/', {signal:lifetimeController.signal}).signal;
                signal.addEventListener('abort', () => lifetimeEvents.push('listener'));
                globalThis.observedSignal = new WeakRef(signal);
                const handler = new Request('https://example.com/', {signal:lifetimeController.signal}).signal;
                handler.onabort = () => lifetimeEvents.push('handler');
                globalThis.handlerSignal = new WeakRef(handler);
            })();
            """);
        rt.Isolate.CollectGarbage(true);
        var result = rt.Evaluate(
            """
            (() => {
                const live = unobservedSignals.filter(ref => ref.deref() !== undefined).length;
                const observed = observedSignal.deref() !== undefined && handlerSignal.deref() !== undefined;
                lifetimeController.abort();
                return {live,observed,events:lifetimeEvents};
            })()
            """);
        AssertJsonEquals("""{"live": 0, "observed": true, "events": ["listener","handler"]}""", result);
    }

    /// <summary>An interception consumer that fulfils every request with a fixed body.</summary>
    private sealed class FulfillingSink(string body) : IInterceptSink
    {
        public bool TrySend(InterceptedRequest request)
        {
            request.Resolver.TrySetResult(new InterceptResolution.Fulfill(
                200, new Dictionary<string, string>(StringComparer.Ordinal), body));
            return true;
        }
    }

    [Fact]
    public async Task FetchAbortErrorsFulfilledResponseClonesWithoutNativeBodyResources()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.SetInterceptSink(new FulfillingSink("fulfilled body"));
        rt.SetInterceptEnabled(true);
        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                const controller = new AbortController();
                const response = await fetch('https://example.com/resource', {signal:controller.signal});
                const copy = response.clone();
                const lazy = copy.clone();
                const reader = copy.body.getReader();
                const closed = reader.closed.then(() => false, error => error.name === 'AbortError');
                const original = await response.text();
                controller.abort();
                return [original, await reader.read().then(() => false, error => error.name === 'AbortError'),
                    await closed, await lazy.text().then(() => false, error => error.name === 'AbortError')];
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("""["fulfilled body", true, true, true]""", result.Value);
        Assert.Equal(0, rt.State.PageInFlight.Value);
    }

    [Fact]
    public void AbortSignalGeneratedEventsAreTrustedButAuthorEventsAreNot()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const controller = new AbortController();
                const signal = controller.signal;
                const events = [];
                signal.addEventListener('abort', event => events.push({
                    trusted:event.isTrusted,
                    bubbles:event.bubbles, cancelable:event.cancelable
                }));
                signal.dispatchEvent(new Event('abort'));
                controller.abort(); controller.abort();
                return events;
            })()
            """);
        AssertJsonEquals(
            """
            [
                {"trusted":false,"bubbles":false,"cancelable":false},
                {"trusted":true,"bubbles":false,"cancelable":false}
            ]
            """,
            result);
    }

    [Fact]
    public async Task FetchAbortDoesNotCallOverriddenPublicSignalEventMethods()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const controller = new AbortController();
                controller.signal.addEventListener = controller.signal.removeEventListener = () => {
                    throw new Error('public event method invoked');
                };
                const fetching = fetch('data:text/plain,complete', {signal:controller.signal});
                controller.abort();
                return fetching.then(() => false, error => error === controller.signal.reason);
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("true", result.Value);
        Assert.Equal(0, fixture.Runtime.State.PageInFlight.Value);
    }

    /// <summary>
    /// Rust <c>check_fetch_abort_transport</c>: a server that never finishes its response,
    /// optionally after sending the headers. Aborting must reject with AbortError and close
    /// the connection, and report one failed request.
    /// </summary>
    /// <remarks>
    /// The port's op still returns the whole body (upstream 3aa2f59 is not ported), so after
    /// the headers fetch() has not resolved yet: the test aborts once the server has sent
    /// them rather than once script sees the response. The outcome is the same AbortError.
    /// </remarks>
    private static async Task CheckFetchAbortTransportAsync(bool afterHeaders)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            if (afterHeaders)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n"));
                await stream.FlushAsync();
            }

            accepted.TrySetResult();
            // No timed response: cancellation must actually close this socket.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                return await stream.ReadAsync(buffer, timeout.Token) == 0;
            }
            catch (IOException)
            {
                return true; // reset by the client
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        });

        try
        {
            using var fixture = LoopbackPage(origin);
            var rt = fixture.Runtime;
            rt.ExecuteScript(
                "start-abort-fetch",
                $$"""
                globalThis.abortController = new AbortController();
                globalThis.abortOutcome = null;
                fetch('{{origin}}/resource', { signal: abortController.signal })
                    .then(response => response.text())
                    .then(() => abortOutcome = 'resolved', error => abortOutcome = error.name);
                """);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!accepted.Task.IsCompleted && clock.ElapsedMilliseconds < EventLoopWait.GenerousMs)
            {
                await rt.RunEventLoopBoundedAsync(10);
            }

            Assert.True(accepted.Task.IsCompleted, "the request must start before cancellation");
            rt.ExecuteScript("abort-fetch", "abortController.abort();");
            Assert.True(
                await EventLoopWait.UntilAsync(rt, "abortOutcome !== null"),
                "abort must settle without a server response");
            Assert.Equal("AbortError", rt.Evaluate("abortOutcome")!.GetValue<string>());
            Assert.True(await server, "abort must stop native network work, not race a JS promise");
            Assert.Equal(0, rt.State.PageInFlight.Value);
            Assert.Equal(0, FetchConcurrency.InFlight(rt.State));
            var events = rt.TakeJsNetworkEvents();
            Assert.Single(events);
            Assert.Equal(0, events[0].Status);
            Assert.Equal("net::ERR_ABORTED", events[0].ErrorText);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public Task FetchAbortCancelsTransportBeforeResponseHeaders() => CheckFetchAbortTransportAsync(false);

    [Fact]
    public Task FetchAbortCancelsTransportAndBodyAfterResponseHeaders() => CheckFetchAbortTransportAsync(true);

    [Fact]
    public async Task FetchAbortReleasesItsCancelHandle()
    {
        using var server = new RawHttpServer(_ => AbortOkResponse);
        using var fixture = LoopbackPage(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const controller = new AbortController();
                const response = await fetch('{{server.Origin}}/a', {signal: controller.signal});
                return await response.text();
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("\"ok\"", result.Value);
        Assert.Empty(fixture.Runtime.State.FetchCancellations);
        var events = fixture.Runtime.TakeJsNetworkEvents();
        Assert.Single(events);
        Assert.Null(events[0].ErrorText);
    }

    [Fact]
    public async Task ReadableStreamErrorDiscardsQueuedChunksAfterCloseRequested()
    {
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const reason = { cancelled: true };
                const results = [];
                for (const requestClose of [false, true]) {
                    let controller;
                    const stream = new ReadableStream({start(c) { controller = c; }});
                    controller.enqueue('queued');
                    if (requestClose) controller.close();
                    const reader = stream.getReader();
                    const closed = reader.closed.then(() => false, error => error === reason);
                    controller.error(reason);
                    results.push(await reader.read().then(() => false, error => error === reason));
                    results.push(await closed);
                }
                return results;
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("[true, true, true, true]", result.Value);
    }

    [Fact]
    public async Task ReadableStreamClosesOnlyAfterQueuedChunksAreDrained()
    {
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                let controller;
                const stream = new ReadableStream({start(c) { controller = c; }});
                controller.enqueue('first'); controller.enqueue('last'); controller.close();
                const reader = stream.getReader();
                let closed = false;
                const completion = reader.closed.then(() => closed = true);
                await Promise.resolve();
                const states = [closed];
                const first = await reader.read();
                states.push(closed);
                const last = await reader.read();
                await completion;
                states.push(closed);
                controller.error('too late');
                const end = await reader.read();
                return {states, chunks:[first.value, last.value], done:end.done};
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals(
            """{"states": [false, false, true], "chunks": ["first", "last"], "done": true}""",
            result.Value);
    }
}

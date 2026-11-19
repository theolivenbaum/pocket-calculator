using System.Net;
using System.Net.Sockets;
using System.Text;
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 3aa2f59: fetch() resolves with the headers, before the body has arrived, and
/// the body is a resource the response's clones share. The expected values were measured
/// in Chromium (playwright chromium, an in-page fetch against a local server that holds the
/// body until released).
/// </summary>
public sealed partial class RuntimeTests
{
    /// <summary>
    /// Rust <c>held_fetch_body_runtime</c>: a page whose <c>fetch()</c> of a server that sends
    /// the headers and then holds the four-byte body until <c>Release</c> is completed.
    /// </summary>
    private sealed class HeldFetchBody : IDisposable
    {
        private readonly TcpListener listener;

        public HeldFetchBody(bool startFetch = true)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            Server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                _ = await stream.ReadAsync(new byte[4096]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n"));
                await stream.FlushAsync();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
                try
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("tail"));
                    await stream.FlushAsync();
                    // Hold the connection until the client is done with it.
                    _ = await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
                {
                    // A cancelled body closed the connection first.
                }
            });
            Origin = origin;
            Fixture = LoopbackPage(origin);
            if (!startFetch)
            {
                return;
            }
            Fixture.Runtime.ExecuteScript(
                "header-first-fetch",
                $"globalThis.headerResponse = null; fetch('{origin}/resource').then(r => headerResponse = r);");
        }

        public RuntimeFixture Fixture { get; }

        public string Origin { get; }

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Server { get; }

        public Task<bool> HeadersAsync() => EventLoopWait.UntilAsync(Fixture.Runtime, "headerResponse !== null");

        public void Dispose()
        {
            Release.TrySetResult();
            Fixture.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task FetchResolvesHeadersBeforeTheResponseBodyFinishes()
    {
        using var held = new HeldFetchBody();
        var rt = held.Fixture.Runtime;
        var headersFirst = await held.HeadersAsync();
        var pendingBody = rt.State.PageInFlight.Value;
        // Release and verify the bytes even on RED, so the fixture never hangs.
        held.Release.SetResult();
        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                const r = headerResponse || await new Promise(resolve => {
                    const check = () => headerResponse ? resolve(headerResponse) : setTimeout(check, 1);
                    check(); });
                const unread = !r.bodyUsed;
                return { status: r.status, unread, text: await r.text(), used: r.bodyUsed };
            }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("""{"status": 200, "unread": true, "text": "tail", "used": true}""", result.Value);
        Assert.True(headersFirst, "fetch must expose headers while the body is still held");
        Assert.Equal(1, pendingBody);
        Assert.Equal(0, rt.State.PageInFlight.Value);
        Assert.Equal(0, FetchConcurrency.InFlight(rt.State));
        Assert.Empty(rt.State.FetchBodies);
        // One network event, under the id the response carried, with the body for CDP.
        var events = rt.TakeJsNetworkEvents();
        Assert.Single(events);
        Assert.Null(events[0].ErrorText);
        Assert.Equal(4, events[0].BodySize);
        Assert.True(rt.State.NetworkResponseBodies.ContainsKey(events[0].RequestId));
    }

    [Fact]
    public async Task RepeatedFetchCloneCancellationPreservesTheOtherBody()
    {
        using var held = new HeldFetchBody();
        var rt = held.Fixture.Runtime;
        var headersFirst = await held.HeadersAsync();
        if (headersFirst)
        {
            AssertJsonEquals(
                "[false, true]",
                rt.Evaluate(
                    """
                    (() => {
                        const copy = headerResponse.clone(); const stream = copy.body;
                        stream.cancel(); stream.cancel();
                        return [headerResponse.bodyUsed, copy.bodyUsed]; })()
                    """));
        }

        held.Release.SetResult();
        var result = await rt.CallFunctionOnForCdpAsync(
            "async () => await headerResponse.text()", null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("\"tail\"", result.Value);
        Assert.True(headersFirst);
        Assert.Equal(0, rt.State.PageInFlight.Value);
    }

    [Fact]
    public async Task FetchCloneBodyBytesAreIndependent()
    {
        using var held = new HeldFetchBody();
        var rt = held.Fixture.Runtime;
        var headersFirst = await held.HeadersAsync();
        if (headersFirst)
        {
            rt.ExecuteScript("clone-held-body", "globalThis.clonedResponse = headerResponse.clone();");
        }

        held.Release.SetResult();
        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                const reader = headerResponse.body.getReader();
                const { value } = await reader.read(); value[0] = 88;
                return [new TextDecoder().decode(value), await clonedResponse.text()]; }
            """,
            null, [], returnByValue: true, awaitPromise: true);
        AssertJsonEquals("""["Xail", "tail"]""", result.Value);
        Assert.True(headersFirst);
        Assert.Equal(0, rt.State.PageInFlight.Value);
    }

    [Fact]
    public async Task CancellingAllFetchClonesReleasesThePendingBody()
    {
        using var held = new HeldFetchBody();
        var rt = held.Fixture.Runtime;
        var headersFirst = await held.HeadersAsync();
        if (headersFirst)
        {
            var result = await rt.CallFunctionOnForCdpAsync(
                """
                async () => {
                    const copy = headerResponse.clone();
                    await copy.body.cancel(); await headerResponse.body.cancel();
                    return [headerResponse.bodyUsed, copy.bodyUsed]; }
                """,
                null, [], returnByValue: true, awaitPromise: true);
            AssertJsonEquals("[true, true]", result.Value);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Until the read has stopped and its failure has been recorded on the page's loop.
        while ((rt.State.PageInFlight.Value != 0 || rt.State.JsNetworkEvents.Count == 0)
            && clock.ElapsedMilliseconds < EventLoopWait.GenerousMs)
        {
            await rt.RunEventLoopBoundedAsync(10);
        }

        var cancelled = rt.State.PageInFlight.Value == 0;
        held.Release.SetResult();
        Assert.True(headersFirst);
        Assert.True(cancelled, "cancelling every clone must release the held network body");
        Assert.Equal(0, FetchConcurrency.InFlight(rt.State));
        Assert.Empty(rt.State.FetchBodies);
        var events = rt.TakeJsNetworkEvents();
        Assert.Single(events);
        Assert.Equal("net::ERR_ABORTED", events[0].ErrorText);
    }

    [Fact]
    public async Task FetchBodyOpRefusesUnknownAndTakenBodies()
    {
        using var held = new HeldFetchBody(startFetch: false);
        var rt = held.Fixture.Runtime;
        await Assert.ThrowsAsync<OpException>(() => FetchOps.OpFetchBodyAsync(rt.State, 12345));
        // The op's tails run on the page's loop, which the test drives.
        var raw = await PumpAsync(rt, FetchOps.OpFetchUrlAsync(
            rt.State, held.Origin + "/a", "GET", "{}", [], string.Empty, "cors", "same-origin",
            tailOnPageLoop: true, bodyResource: true));
        Assert.DoesNotContain("\"body\"", raw, StringComparison.Ordinal);
        Assert.Matches("""^\{"status":200,"requestId":"fetch-\d+","url":"[^"]+","redirected":false,"opaque":false,"headers":\{.*\},"bodyRid":\d+\}$""", raw);
        var rid = uint.Parse(System.Text.RegularExpressions.Regex.Match(raw, "\"bodyRid\":(\\d+)").Groups[1].Value);
        var first = FetchOps.OpFetchBodyAsync(rt.State, rid);
        var second = FetchOps.OpFetchBodyAsync(rt.State, rid);
        held.Release.TrySetResult();
        Assert.Equal(Convert.ToBase64String("tail"u8.ToArray()), await PumpAsync(rt, first));
        // Taken while the first read runs, or gone once it closed the resource.
        await Assert.ThrowsAsync<OpException>(() => second);
        Assert.Empty(rt.State.FetchBodies);
    }

    // DEVIATION from upstream 3aa2f59 (see FetchUrlAsync): a body already complete when the
    // headers arrive is returned inline, in one op, with no body resource.
    [Fact]
    public async Task AFetchBodyCompleteWithItsHeadersIsReturnedInline()
    {
        using var server = new RawHttpServer(_ => AbortOkResponse);
        using var fixture = LoopbackPage(server.Origin);
        var rt = fixture.Runtime;
        // The op's tails run on the page's loop, which the test drives.
        var raw = await PumpAsync(rt, FetchOps.OpFetchUrlAsync(
            rt.State, server.Origin + "/a", "GET", "{}", [], string.Empty, "cors", "same-origin",
            tailOnPageLoop: true, bodyResource: true));
        Assert.Contains("\"body\":\"ok\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bodyRid\"", raw, StringComparison.Ordinal);
        Assert.Empty(rt.State.FetchBodies);
    }

    private static async Task<T> PumpAsync<T>(PocketCalculator.Js.Runtime.PocketCalculatorJsRuntime rt, Task<T> task)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < EventLoopWait.GenerousMs)
        {
            await rt.RunEventLoopBoundedAsync(10);
            await Task.WhenAny(task, Task.Delay(1));
        }

        return await task;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>Port of <c>crates/obscura-mcp/tests/http_page_progress.rs</c> (upstream 5c27e76).</summary>
public sealed class HttpPageProgressTests
{
    private static async Task<JsonNode> RpcAsync(ushort port, string method, JsonObject parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }
            .ToJsonString();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        var request = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        var json = JsonNode.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..])!;
        Assert.Null(json["error"]);
        var result = json["result"]!;
        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        return result;
    }

    private static JsonObject Tool(string name, JsonObject arguments) =>
        new() { ["name"] = name, ["arguments"] = arguments };

    /// <summary>A one-shot HTTP fixture that reports the request line it received.</summary>
    private static (int Port, Task<string> Request) OneShot(string response, TimeSpan delay = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                while (await reader.ReadLineAsync() is { Length: > 0 })
                {
                }
                started.TrySetResult(requestLine);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception error)
            {
                started.TrySetException(error);
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, started.Task);
    }

    [Fact]
    public async Task Page_callbacks_progress_without_more_http_tool_calls()
    {
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync("127.0.0.1", port, null, null, false, cts.Token));
        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);

            var tabs = await RpcAsync(port, "tools/call", Tool("browser_tab_list", []));
            // An unarmed pump must not create a page.
            Assert.Equal("No tabs open.", tabs["content"]![0]!["text"]!.GetValue<string>());
            await RpcAsync(port, "tools/call", Tool("browser_navigate", new() { ["url"] = "data:text/html,<p>Waiting</p>" }));
            // A distant timer must remain preemptible by a new RPC, not hold the owner loop.
            await RpcAsync(port, "tools/call", Tool("browser_evaluate", new()
            {
                ["expression"] = "(function(){globalThis.distantTimer=setTimeout(()=>{},60000);return 'armed';})()",
            }));
            await RpcAsync(port, "ping", []);

            var (fixturePort, arrived) = OneShot(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
            await RpcAsync(port, "tools/call", Tool("browser_evaluate", new()
            {
                ["expression"] = $"(function(){{clearTimeout(distantTimer);setTimeout(()=>fetch('http://127.0.0.1:{fixturePort}/idle-callback'),30);return 'queued';}})()",
            }));
            // No tool call pumps the page here. Only the HTTP owner can run the timer/fetch.
            var request = await arrived.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.StartsWith("GET /idle-callback ", request, StringComparison.Ordinal);
            await RpcAsync(port, "ping", []);

            var (navigationPort, navigating) = OneShot(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 13\r\nConnection: close\r\n\r\n<p>Landed</p>",
                TimeSpan.FromMilliseconds(100));
            await RpcAsync(port, "tools/call", Tool("browser_evaluate", new()
            {
                ["expression"] = $"(function(){{setTimeout(()=>location.href='http://127.0.0.1:{navigationPort}/landed',30);return 'queued';}})()",
            }));
            await navigating.WaitAsync(TimeSpan.FromSeconds(3));
            await RpcAsync(port, "ping", []);
            var snapshot = await RpcAsync(port, "tools/call", Tool("browser_snapshot", []));
            Assert.EndsWith("\n\nLanded", snapshot["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

            await RpcAsync(port, "tools/call", Tool("browser_close", []));
            tabs = await RpcAsync(port, "tools/call", Tool("browser_tab_list", []));
            // Idle driving must not reopen a closed page.
            Assert.Equal("No tabs open.", tabs["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

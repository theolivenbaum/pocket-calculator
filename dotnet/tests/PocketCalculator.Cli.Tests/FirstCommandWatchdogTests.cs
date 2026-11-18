using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura-cli/tests/first_command_watchdog.rs</c> (upstream e83d803). A
/// script command can create or resume its page's isolate inside the handler; the CDP
/// watchdog must bound that first call too. Here every page but the active one is
/// suspended, so each case creates a second target and drives the first, suspended one.
/// </summary>
public sealed class FirstCommandWatchdogTests
{
    [Theory]
    [InlineData("Runtime.evaluate", false)]
    [InlineData("Runtime.evaluate", true)]
    [InlineData("Runtime.callFunctionOn", false)]
    [InlineData("Runtime.callFunctionOn", true)]
    public async Task First_script_command_on_a_suspended_page_has_a_watchdog(string method, bool awaitPromise)
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        int port = PickPort();
        var psi = new ProcessStartInfo(CliProcess.Binary!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in (string[])["serve", "--port", port.ToString(CultureInfo.InvariantCulture)])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["POCKETCALCULATOR_CDP_COMMAND_TIMEOUT_MS"] = "300";
        using Process server = Process.Start(psi) ?? throw new InvalidOperationException("serve did not start");
        _ = server.StandardOutput.ReadToEndAsync();
        _ = server.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using ClientWebSocket ws = await ConnectWhenListeningAsync(port, deadline.Token);
            ulong next = 0;
            async Task<JsonNode> CallAsync(string m, JsonObject parameters, string? session = null, TimeSpan? within = null)
            {
                ulong id = ++next;
                var message = new JsonObject { ["id"] = id, ["method"] = m, ["params"] = parameters };
                if (session is not null)
                {
                    message["sessionId"] = session;
                }

                await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token);
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                reply.CancelAfter(within ?? TimeSpan.FromSeconds(20));
                while (true)
                {
                    string text;
                    try
                    {
                        text = await ReceiveAsync(ws, reply.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"no response to {m}; the first command must be watchdog bounded");
                    }

                    JsonNode value = JsonNode.Parse(text)!;
                    if (value["id"]?.GetValue<ulong>() == id)
                    {
                        return value;
                    }
                }
            }

            JsonNode first = await CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" });
            await CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" });
            JsonNode attached = await CallAsync(
                "Target.attachToTarget",
                new JsonObject { ["targetId"] = first["result"]!["targetId"]!.GetValue<string>(), ["flatten"] = true });
            string session = attached["result"]!["sessionId"]!.GetValue<string>();

            // No Runtime.enable or warm-up evaluation: those would hide the bug.
            const string source = "globalThis.started=true;while(true){}";
            var parameters = new JsonObject { ["returnByValue"] = true, ["awaitPromise"] = awaitPromise, ["timeout"] = 10000 };
            if (method == "Runtime.evaluate")
            {
                parameters["expression"] = source;
            }
            else
            {
                parameters["functionDeclaration"] = "function(){" + source + "}";
            }

            JsonNode interrupted = await CallAsync(method, parameters, session, TimeSpan.FromSeconds(15));
            string error = interrupted.ToJsonString().ToLowerInvariant();
            Assert.True(
                interrupted["error"] is not null || interrupted["result"]?["exceptionDetails"] is not null,
                $"{method}: {interrupted.ToJsonString()}");
            Assert.True(
                error.Contains("terminat", StringComparison.Ordinal) || error.Contains("timeout", StringComparison.Ordinal)
                    || error.Contains("timed out", StringComparison.Ordinal) || error.Contains("exceeded", StringComparison.Ordinal)
                    || error.Contains("interrupt", StringComparison.Ordinal),
                $"expected a watchdog interruption: {interrupted.ToJsonString()}");

            JsonNode after = await CallAsync(
                "Runtime.evaluate",
                new JsonObject { ["expression"] = "[1+1,globalThis.started]", ["returnByValue"] = true },
                session);
            Assert.Equal("[2,true]", after["result"]!["result"]!["value"]!.ToJsonString());
        }
        finally
        {
            try
            {
                server.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            await server.WaitForExitAsync();
        }
    }

    private static int PickPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        probe.Listen(1);
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<ClientWebSocket> ConnectWhenListeningAsync(int port, CancellationToken ct)
    {
        while (true)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/browser"), ct);
                return ws;
            }
            catch (WebSocketException)
            {
                ws.Dispose();
                await Task.Delay(100, ct);
            }
        }
    }

    private static async Task<string> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new List<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("server closed the connection");
            }

            message.AddRange(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString([.. message]);
            }
        }
    }
}

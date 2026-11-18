using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Port of upstream <c>crates/obscura-cli/tests/serve_worker_recycle.rs</c>
/// (0f27e69): <c>serve --workers N --worker-max-*</c> replaces workers that pass
/// a threshold without failing any session: replacements keep the pool at N, and
/// a replaced worker keeps serving the connections it already had until they close.
/// </summary>
/// <remarks>
/// The deadlines are wider than upstream's: a .NET worker takes longer to start
/// than the Rust one, and a replacement is verified before it takes traffic.
/// </remarks>
[Collection(ServeProcess.Collection)]
public sealed class ServeWorkerRecycleTests
{
    private const string Page = "data:text/html,<title>recycle</title><body>ok</body>";

    private static ServeProcess Start(params string[] extra)
    {
        ServeProcess.RequireLinux();
        var server = ServeProcess.Start(2, extra);
        var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        while (server.Workers().Count != 2 || !server.DiscoveryOk())
        {
            Assert.False(server.Process.HasExited, $"balancer exited:\n{server.Log}");
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"balancer did not become ready:\n{server.Log}");
            Thread.Sleep(10);
        }
        return server;
    }

    /// <summary>Wait until exactly <paramref name="count"/> workers exist and none of <paramref name="gone"/> is alive.</summary>
    private static void Settle(ServeProcess server, int count, IReadOnlyList<int> gone, TimeSpan within)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var now = server.Workers();
            if (now.Count == count && gone.All(pid => !now.Contains(pid)))
            {
                return;
            }
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"workers [{string.Join(", ", now)}], expected {count} without [{string.Join(", ", gone)}]\n{server.Log}");
            Thread.Sleep(20);
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ClientWebSocket ws;
        private ulong id;
        private string sid = string.Empty;

        private Session(ClientWebSocket ws) => this.ws = ws;

        public async Task<JsonNode> CallAsync(string method, JsonObject parameters, bool withSession)
        {
            var callId = ++id;
            var message = new JsonObject { ["id"] = callId, ["method"] = method, ["params"] = parameters };
            if (withSession)
            {
                message["sessionId"] = sid;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token);
            while (true)
            {
                var reply = JsonNode.Parse(await ReceiveAsync(timeout.Token))!;
                if (reply["id"]?.GetValue<ulong>() == callId)
                {
                    Assert.True(reply["error"] is null, $"{method}: {reply.ToJsonString()}");
                    return reply;
                }
            }
        }

        private async Task<string> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var message = new List<byte>();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("ws closed");
                }
                message.AddRange(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString([.. message]);
                }
            }
        }

        /// <summary>A full CDP session: connect, open a page, navigate to <paramref name="url"/>.</summary>
        public static async Task<Session> OpenAsync(int port, string url)
        {
            var ws = new ClientWebSocket();
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/browser"), connect.Token);
            }
            var s = new Session(ws);
            var target = await s.CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, false);
            var attached = await s.CallAsync(
                "Target.attachToTarget",
                new JsonObject
                {
                    ["targetId"] = target["result"]!["targetId"]!.GetValue<string>(),
                    ["flatten"] = true,
                },
                false);
            s.sid = attached["result"]!["sessionId"]!.GetValue<string>();
            await s.CallAsync("Page.enable", [], true);
            await s.CallAsync("Page.navigate", new JsonObject { ["url"] = url }, true);
            return s;
        }

        public async Task<JsonNode?> EvalAsync(string expression)
        {
            var reply = await CallAsync(
                "Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true }, true);
            return reply["result"]!["result"]!["value"];
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException
                or InvalidOperationException)
            {
            }
            ws.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionLimitReplacesWorkersWithoutFailingSessions()
    {
        using var server = Start("--worker-max-connections", "3");
        var original = server.Workers();
        var seen = new HashSet<int>(original);

        // A long-lived session on one worker: it must survive that worker being
        // recycled underneath it.
        var held = await Session.OpenAsync(server.Port, Page);

        // Sessions arrive faster than a replacement starts, so keep going until
        // several generations of workers have come and gone.
        var deadline = Stopwatch.GetTimestamp() + (90 * Stopwatch.Frequency);
        var i = 0;
        while (seen.Count < 8)
        {
            i++;
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"workers were not replaced: [{string.Join(", ", seen)}]\n{server.Log}");
            await using (var s = await Session.OpenAsync(server.Port, Page))
            {
                Assert.Equal("recycle", (await s.EvalAsync("document.title"))?.GetValue<string>());
            }
            seen.UnionWith(server.Workers());
        }
        Assert.Equal("recycle", (await held.EvalAsync("document.title"))?.GetValue<string>());

        // The worker that carried `held` outlives its replacement until it closes.
        var now = server.Workers();
        Assert.True(original.Any(now.Contains), $"the held session's worker was killed: [{string.Join(", ", now)}]");
        await held.DisposeAsync();
        // Pool capacity is unchanged and the replaced originals are gone.
        Settle(server, 2, original, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RssLimitReplacesABloatedWorkerAndKeepsItsSession()
    {
        // Upstream sets 150 MB; an idle .NET worker is already about 140 MB, so the
        // limit is raised to keep the same margin over a worker that is not bloated.
        using var server = Start("--worker-max-rss", "400");
        var original = server.Workers();
        // ~300 MB held live by the page's isolate, well over the limit.
        const string bloat =
            "data:text/html,<title>big</title><script>window.keep=new Uint8Array(300*1024*1024).fill(7)</script>";
        var held = await Session.OpenAsync(server.Port, bloat);

        var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        List<int> replaced;
        while (true)
        {
            var now = server.Workers();
            if (original.FirstOrDefault(pid => !now.Contains(pid)) is var gone and not 0)
            {
                Assert.Fail($"worker {gone} was killed while its session was live\n{server.Log}");
            }
            if (now.Count == 3)
            {
                replaced = now;
                break;
            }
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"bloated worker was not replaced: [{string.Join(", ", now)}]\n{server.Log}");
            Thread.Sleep(100);
        }
        // Draining: the replacement exists, the bloated worker still answers.
        Assert.Equal(7, (await held.EvalAsync("window.keep[1000]"))!.GetValue<double>());
        // New sessions work and never land on the draining worker.
        for (var i = 0; i < 4; i++)
        {
            await using var s = await Session.OpenAsync(server.Port, Page);
            Assert.Equal("recycle", (await s.EvalAsync("document.title"))?.GetValue<string>());
        }
        await held.DisposeAsync();
        Assert.Equal(2, replaced.Count(original.Contains));
        // Exactly one original (the bloated one) leaves; capacity returns to two.
        deadline = Stopwatch.GetTimestamp() + (15 * Stopwatch.Frequency);
        while (true)
        {
            var now = server.Workers();
            if (now.Count == 2 && original.Count(now.Contains) == 1)
            {
                break;
            }
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"drained worker not retired: [{string.Join(", ", now)}]\n{server.Log}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownStopsAWorkerThatIsStillDraining()
    {
        using var server = Start("--worker-max-connections", "1", "--worker-drain-timeout", "300");
        var original = server.Workers();
        // The held session's worker reaches its limit and is replaced, but keeps
        // running for up to 300 s while the session stays open.
        var held = await Session.OpenAsync(server.Port, Page);
        var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        List<int> all;
        while (true)
        {
            var now = server.Workers();
            if (now.Count == 3)
            {
                all = now;
                break;
            }
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"worker was not replaced: [{string.Join(", ", now)}]\n{server.Log}");
            Thread.Sleep(20);
        }
        Assert.True(original.All(all.Contains), $"draining worker was killed: [{string.Join(", ", all)}]");
        Assert.Equal("recycle", (await held.EvalAsync("document.title"))?.GetValue<string>());

        // Signal only the balancer: every worker, the draining one included, must go.
        ServeProcess.Kill("-TERM", server.Process.Id);
        Assert.True(server.Process.WaitForExit(10_000), $"balancer ignored SIGTERM\n{server.Log}");
        deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
        while (all.Any(ServeProcess.Alive))
        {
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"workers survived SIGTERM: [{string.Join(", ", all)}]\n{server.Log}");
            Thread.Sleep(10);
        }
        await held.DisposeAsync();
    }
}

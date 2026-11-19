using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PocketCalculator.Mcp;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebSearchEnvCollection
{
    public const string Name = "web-search-env";
}

/// <summary>Ports of upstream 9855ef4's lib.rs tests for the opt-in <c>web_search</c> tool.</summary>
[Collection(WebSearchEnvCollection.Name)]
public sealed class WebSearchTests
{
    private const string Env = "POCKETCALCULATOR_WEB_SEARCH";

    /// <summary>One loopback exchange: reads the request, writes <paramref name="response"/>.</summary>
    private static (string Url, Task Server) Fixture(string response, bool holdOpen)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/search/public";
        Task server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            Assert.Equal("POST /v1/search/public HTTP/1.1", await reader.ReadLineAsync());
            int length = 0;
            for (string? line; (line = await reader.ReadLineAsync()) is { Length: > 0 };)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            var body = new char[length];
            await reader.ReadBlockAsync(body);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                if (holdOpen)
                {
                    // Leave the response incomplete until the client drops it.
                    _ = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception error) when (error is IOException or SocketException or TimeoutException)
            {
            }
            finally
            {
                listener.Stop();
            }
        });
        return (url, server);
    }

    private static PocketCalculatorHttpClient LoopbackClient() => new(new CookieJar(), null, true);

    [Fact]
    public async Task Web_search_response_limit_accepts_normal_and_fifty_result_bodies()
    {
        string small = new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["title"] = "Small fixture", ["url"] = "https://example.test/", ["snippet"] = "small result" }),
        }.ToJsonString();
        var results = new JsonArray();
        for (int i = 0; i < 50; i++)
        {
            results.Add(new JsonObject { ["title"] = $"Result {i}", ["url"] = $"https://example.test/{i}", ["snippet"] = string.Concat(Enumerable.Repeat("word ", 1600)) });
        }
        string boundary = new JsonObject { ["results"] = results }.ToJsonString();
        Assert.True(boundary.Length < Tools.WebSearchResponseBytes);
        boundary += new string(' ', Tools.WebSearchResponseBytes - boundary.Length);

        foreach ((string body, int count) in new[] { (small, 1), (boundary, 50) })
        {
            foreach (bool chunked in new[] { false, true })
            {
                string response = chunked
                    ? $"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{body.Length:X}\r\n{body}\r\n0\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                var (url, server) = Fixture(response, false);
                using var client = LoopbackClient();
                var request = Tools.BuildWebSearchRequest("obscura", 50, null) with { Url = url };
                JsonNode result = await Tools.FetchWebSearchAsync(request, client, true).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(count, result["results"]!.AsArray().Count);
                Assert.Equal(count, Tools.FormatWebResults("obscura", result).Split('\n').Length);
                if (count == 1)
                {
                    Assert.Equal("small result", result["results"]![0]!["snippet"]!.GetValue<string>());
                }
                else
                {
                    Assert.Equal("Result 49", result["results"]![49]!["title"]!.GetValue<string>());
                    Assert.Equal(8000, result["results"]![49]!["snippet"]!.GetValue<string>().Length);
                }
                await server.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Web_search_response_limit_rejects_overflow_before_eof()
    {
        int limit = Tools.WebSearchResponseBytes;
        string body = "{\"results\":[]}";
        body += new string(' ', limit + 1 - body.Length);
        string[] responses =
        [
            $"HTTP/1.1 200 OK\r\nContent-Length: {limit + 1}\r\nConnection: close\r\n\r\n",
            $"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{body.Length:X}\r\n{body}\r\n",
            $"HTTP/1.1 429 Too Many Requests\r\nConnection: close\r\n\r\n{body}",
        ];
        foreach (string response in responses)
        {
            var (url, server) = Fixture(response, true);
            using var client = LoopbackClient();
            var request = Tools.BuildWebSearchRequest("obscura", 50, null) with { Url = url };
            var error = await Assert.ThrowsAsync<ToolException>(
                () => Tools.FetchWebSearchAsync(request, client, true).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("web search response failed:", error.Message, StringComparison.Ordinal);
            Assert.Contains($"exceeded {limit} byte limit", error.Message, StringComparison.Ordinal);
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Web_search_is_hidden_and_rejected_without_opt_in()
    {
        Environment.SetEnvironmentVariable(Env, null);
        RpcResponse list = await McpServer.DispatchAsync("tools/list", JsonNode.Parse("1"), null, new BrowserState(null, null, false));
        Assert.DoesNotContain("\"web_search\"", list.Result!.ToJsonString(), StringComparison.Ordinal);
        using var state = new BrowserState(null, null, false);
        RpcResponse call = await McpServer.DispatchAsync("tools/call", JsonNode.Parse("1"),
            JsonNode.Parse("""{"name":"web_search","arguments":{"query":"obscura"}}"""), state);
        Assert.True(call.Result!["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown tool: web_search", call.Result!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_search_is_advertised_when_enabled_and_requires_a_query()
    {
        Environment.SetEnvironmentVariable(Env, "keenable");
        try
        {
            RpcResponse list = await McpServer.DispatchAsync("tools/list", JsonNode.Parse("1"), null, new BrowserState(null, null, false));
            JsonNode tool = list.Result!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "web_search")!;
            Assert.Equal("[\"query\"]", tool["inputSchema"]!["required"]!.ToJsonString());
            using var state = new BrowserState(null, null, false);
            var error = await Assert.ThrowsAsync<ToolException>(() => Tools.WebSearchAsync(JsonNode.Parse("""{"query":"  "}"""), state));
            Assert.Contains("Missing query", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Env, null);
        }
    }

    [Theory]
    [InlineData("1", 1)] [InlineData("1.0", 1)] [InlineData("1e0", 1)]
    [InlineData("50", 50)] [InlineData("50.0", 50)] [InlineData("5e1", 50)]
    [InlineData("0", 1)] [InlineData("0.0", 1)] [InlineData("-0.0", 1)]
    [InlineData("51", 50)] [InlineData("51.0", 50)]
    [InlineData("18446744073709551615", 50)] [InlineData("1e100", 50)]
    [InlineData("-1", 5)] [InlineData("-1.0", 5)] [InlineData("1.5", 5)]
    [InlineData("null", 5)] [InlineData("\"1\"", 5)] [InlineData("true", 5)]
    public void Web_search_request_honors_integer_valued_json_limits(string limit, int expected)
    {
        Assert.Equal(5UL, Tools.WebSearchLimit(JsonNode.Parse("{}")));
        var request = Tools.BuildWebSearchRequest("obscura", Tools.WebSearchLimit(JsonNode.Parse($$"""{"limit":{{limit}}}""")), null);
        Assert.Equal(expected, JsonNode.Parse(request.Body)!["max_results"]!.GetValue<int>());
    }

    [Fact]
    public void Web_search_request_is_keyless_unless_a_key_is_set()
    {
        var open = Tools.BuildWebSearchRequest("rust headless browser", 5, null);
        Assert.Equal("https://api.keenable.ai/v1/search/public", open.Url);
        Assert.Contains(("x-keenable-title", "Obscura"), open.Headers);
        Assert.DoesNotContain(open.Headers, h => h.Name == "x-api-key");
        Assert.Equal("""{"query":"rust headless browser","max_results":5}""", open.Body);
        var keyed = Tools.BuildWebSearchRequest("rust", 3, "test-key");
        Assert.Equal("https://api.keenable.ai/v1/search", keyed.Url);
        Assert.Contains(("x-api-key", "test-key"), keyed.Headers);
    }

    [Fact]
    public void Web_search_results_use_snippet_and_stay_compact()
    {
        string longText = string.Concat(Enumerable.Repeat("word ", 400));
        var body = new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject { ["title"] = "  Obscura\n docs ", ["url"] = "https://docs.obscura.sh/", ["description"] = "", ["snippet"] = $"Line one\n\n{longText}" },
                new JsonObject { ["title"] = "no url", ["snippet"] = "dropped" }),
        };
        string output = Tools.FormatWebResults("obscura", body);
        var lines = output.Split('\n').Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Single(lines);
        Assert.Equal("Obscura docs", lines[0]["title"]!.GetValue<string>());
        Assert.Equal("https://docs.obscura.sh/", lines[0]["url"]!.GetValue<string>());
        string snippet = lines[0]["snippet"]!.GetValue<string>();
        Assert.StartsWith("Line one word word", snippet, StringComparison.Ordinal);
        Assert.EndsWith("...", snippet, StringComparison.Ordinal);
        Assert.Equal(503, snippet.Length);
        Assert.Equal("No results for \"nothing\".", Tools.FormatWebResults("nothing", JsonNode.Parse("""{"results":[]}""")!));
    }
}

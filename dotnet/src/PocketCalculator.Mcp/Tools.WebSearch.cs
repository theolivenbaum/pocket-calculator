using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketCalculator.Net;

namespace PocketCalculator.Mcp;

/// <summary>
/// Port of upstream 9855ef4: the opt-in <c>web_search</c> tool. It sends the agent's queries
/// to a third-party API (Keenable), so it is listed and callable only when the operator sets
/// <c>POCKETCALCULATOR_WEB_SEARCH=keenable</c> (upstream: <c>OBSCURA_WEB_SEARCH</c>).
/// </summary>
internal static partial class Tools
{
    private const string KeenableSearchUrl = "https://api.keenable.ai/v1/search";
    private const string KeenablePublicSearchUrl = "https://api.keenable.ai/v1/search/public";
    private const int WebSearchSnippetChars = 500;

    // Room for 50 results with multi-KB snippets, bounded before JSON parsing.
    internal const int WebSearchResponseBytes = 1024 * 1024;

    internal static bool WebSearchEnabled() =>
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_WEB_SEARCH") is { } provider
        && provider.Trim().Equals("keenable", StringComparison.OrdinalIgnoreCase);

    internal const string WebSearchTool =
        """
        {
          "name": "web_search",
          "description": "Search the web and return matching pages, one JSON object per line with title, url, and a short snippet. Use it to find pages to open with browser_navigate. Queries are sent to the Keenable search API.",
          "inputSchema": {
            "type": "object",
            "properties": {
              "query": { "type": "string" },
              "limit": { "type": "number", "description": "Max results to return (default 5, max 50)" }
            },
            "required": ["query"]
          }
        }
        """;

    internal sealed record WebSearchRequest(string Url, IReadOnlyList<(string Name, string Value)> Headers, string Body);

    /// <summary>The public endpoint needs no account; a key only raises the rate limit.</summary>
    internal static WebSearchRequest BuildWebSearchRequest(string query, ulong limit, string? apiKey)
    {
        List<(string, string)> headers =
        [
            ("content-type", "application/json"),
            // The provider's attribution header; it names the upstream project, as a wire value.
            ("x-keenable-title", "Obscura"),
        ];
        string url = KeenablePublicSearchUrl;
        if (apiKey is not null)
        {
            headers.Add(("x-api-key", apiKey));
            url = KeenableSearchUrl;
        }
        var body = new JsonObject { ["query"] = query, ["max_results"] = limit };
        return new WebSearchRequest(url, headers, McpJson.Serialize(body));
    }

    internal static ulong WebSearchLimit(JsonNode? args)
    {
        ulong limit = 5;
        if (args.Get("limit") is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue(out double number) && double.IsFinite(number) && number >= 0 && number == Math.Floor(number))
        {
            limit = number >= ulong.MaxValue ? ulong.MaxValue : (ulong)number;
        }
        return Math.Clamp(limit, 1UL, 50UL);
    }

    internal static async Task<string> WebSearchAsync(JsonNode? args, BrowserState state)
    {
        string query = (args.Get("query").AsString() ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            throw new ToolException("Missing query parameter");
        }
        ulong limit = WebSearchLimit(args);
        string? apiKey = Environment.GetEnvironmentVariable("KEENABLE_API_KEY")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = null;
        }
        var request = BuildWebSearchRequest(query, limit, apiKey);
        JsonNode body = await FetchWebSearchAsync(request, state.Context.HttpClient, apiKey is null).ConfigureAwait(false);
        return FormatWebResults(query, body);
    }

    /// <summary>Through the context's client, which carries the proxy, CA roots and SSRF guard.</summary>
    internal static async Task<JsonNode> FetchWebSearchAsync(WebSearchRequest request, PocketCalculatorHttpClient client, bool keyless)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
        message.Content = new StringContent(request.Body, Encoding.UTF8);
        foreach (var (name, value) in request.Headers)
        {
            if (name == "content-type")
            {
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
        HttpResponseMessage response;
        try
        {
            response = await client.RequestClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ToolException($"web search request failed: {PocketCalculatorNetException.ErrorChain(error)}");
        }
        using (response)
        {
            byte[] bytes;
            try
            {
                bytes = await ReadLimitedAsync(response, request.Url).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or PocketCalculatorNetException)
            {
                throw new ToolException($"web search response failed: {error.Message}");
            }
            int status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                string hint = status == 429 && keyless ? " (set KEENABLE_API_KEY for higher rate limits)" : string.Empty;
                throw new ToolException(string.Create(CultureInfo.InvariantCulture, $"web search returned HTTP {status}{hint}"));
            }
            try
            {
                return JsonNode.Parse(bytes) ?? throw new JsonException("null");
            }
            catch (JsonException error)
            {
                throw new ToolException($"web search returned invalid JSON: {error.Message}");
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, string url)
    {
        // A declared length over the limit is refused before reading, as upstream does.
        if (response.Content.Headers.ContentLength is > WebSearchResponseBytes)
        {
            throw PocketCalculatorNetException.ResponseTooLarge(new Uri(url), WebSearchResponseBytes);
        }
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > WebSearchResponseBytes)
            {
                throw PocketCalculatorNetException.ResponseTooLarge(new Uri(url), WebSearchResponseBytes);
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>One JSON object per line, like <c>browser_links</c>.</summary>
    internal static string FormatWebResults(string query, JsonNode body)
    {
        static string Compact(string text, int maxChars)
        {
            string joined = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var runes = joined.EnumerateRunes().ToList();
            if (runes.Count <= maxChars)
            {
                return joined;
            }
            var cut = new StringBuilder();
            foreach (var rune in runes.Take(maxChars))
            {
                cut.Append(rune.ToString());
            }
            return cut.Append("...").ToString();
        }
        List<string> lines = [];
        if (body["results"] is JsonArray results)
        {
            foreach (JsonNode? result in results)
            {
                if (result?["url"] is not JsonValue url || url.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }
                string Field(string name) =>
                    result[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : string.Empty;
                lines.Add(McpJson.Serialize(new JsonObject
                {
                    ["title"] = Compact(Field("title"), 200),
                    ["url"] = url.GetValue<string>(),
                    ["snippet"] = Compact(Field("snippet"), WebSearchSnippetChars),
                }));
            }
        }
        return lines.Count == 0 ? $"No results for {TextExtraction.RustDebugString(query)}." : string.Join("\n", lines);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using PocketCalculator.Mcp;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

// Port of upstream cbc2f97's search_body_maps_offsets_through_unicode_lowercasing (lib.rs).
public class SearchBodyTests
{
    private static List<JsonObject> Results(string output)
    {
        var json = output[(output.IndexOf(". ", StringComparison.Ordinal) + 2)..];
        return [.. json.Split('\n').Select(line => (JsonObject)JsonNode.Parse(line)!)];
    }

    [Fact]
    public void Search_body_maps_offsets_through_unicode_lowercasing()
    {
        const string body = "İstanbul hava durumu";
        var output = Tools.SearchBody(body, "HAVA", caseSensitive: false, context: 5, limit: 10);
        Assert.StartsWith("1 match(es). ", output, StringComparison.Ordinal);
        var result = Assert.Single(Results(output));
        var expected = Encoding.UTF8.GetByteCount(body[..body.IndexOf("hava", StringComparison.Ordinal)]);
        Assert.Equal(expected, result["offset"]!.GetValue<int>());
        Assert.Contains("hava", result["snippet"]!.GetValue<string>().ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_after_a_length_changing_fold_still_report_body_offsets()
    {
        // U+0130 folds to two characters, so every later haystack offset is shifted.
        const string body = "İİ ab İ AB";
        var results = Results(Tools.SearchBody(body, "ab", caseSensitive: false, context: 0, limit: 10));
        Assert.Equal(2, results.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount("İİ "), results[0]["offset"]!.GetValue<int>());
        Assert.Equal(Encoding.UTF8.GetByteCount("İİ ab İ "), results[1]["offset"]!.GetValue<int>());
        Assert.Equal("AB", results[1]["snippet"]!.GetValue<string>());
    }
}

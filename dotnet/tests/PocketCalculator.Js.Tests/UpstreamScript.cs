using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Runs an upstream runtime.rs test script on a page and compares its JSON result with the
/// value Chromium produced for the same page and script.
/// </summary>
internal static class UpstreamScript
{
    public static void AssertChromium(string html, string script, string chromium)
    {
        using var fixture = RuntimeFixture.Setup(html);
        var actual = fixture.Runtime.Evaluate("JSON.stringify(" + script.Trim() + "\n)")!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(chromium), JsonNode.Parse(actual)), actual);
    }
}

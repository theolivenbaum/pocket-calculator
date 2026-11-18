using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The <c>Fetch.enable</c> URL pattern matcher (upstream 61aa8fb) and the pause decision
/// (upstream 2d323dd), from <c>crates/obscura-js/src/ops.rs</c> and
/// <c>crates/obscura-browser/src/page.rs</c>.
/// </summary>
public sealed class FetchPatternTests
{
    // interception_patterns_match_repeated_suffixes_and_escaped_wildcards
    [Theory]
    [InlineData("/a*abc", "/aabcabc", true)]
    [InlineData("*abc", "abcabc", true)]
    [InlineData("/a?c", "/abc", true)]
    [InlineData("/a?c", "/ac", false)]
    [InlineData("/a?c", "/abbc", false)]
    [InlineData(@"/a\?c", "/a?c", true)]
    [InlineData(@"/a\?c", "/abc", false)]
    [InlineData(@"/a\*c", "/a*c", true)]
    [InlineData(@"/a\*c", "/abc", false)]
    [InlineData(@"/a\\c", @"/a\c", true)]
    [InlineData("/a?c", "/aéc", true)]
    [InlineData("/a?c", "/a\U0001F600c", true)]
    [InlineData("", "", true)]
    [InlineData("", "/", false)]
    [InlineData("**", "", true)]
    [InlineData("/a*c", "/ab", false)]
    [InlineData("*ab*bc", "ababbbc", true)]
    [InlineData("*ab*bc", "abbcd", false)]
    [InlineData("*?*", "", false)]
    [InlineData("*?*", "abc", true)]
    [InlineData("ab*cd*ef", "abxxcdyyef", true)]
    [InlineData("ab*cd*ef", "abxxcdyyefzz", false)]
    // url_matches_cdp_pattern_handles_wildcards_across_url_parts
    [InlineData("*://*.gstatic.com/*.woff2", "https://fonts.gstatic.com/s/inter/v18/UcCO3FwrK3iLTcviYwYZ8UA3.woff2", true)]
    [InlineData("*://*.google.com/maps/vt/*", "https://www.google.com/maps/vt/pb=!1m4!1m3", true)]
    [InlineData("https://example.com/assets/*", "https://example.com/assets/app.js", true)]
    [InlineData("https://example.com/assets/*", "https://cdn.example.com/assets/app.js", false)]
    [InlineData("*://*.gstatic.com/*.woff2", "https://fonts.gstatic.com/s/inter/v18/font.woff", false)]
    public void FetchPatternsMatchLikeChromium(string pattern, string url, bool expected) =>
        Assert.Equal(expected, FetchOps.FetchPatternMatches(pattern, url));

    // upstream 2d323dd: scripted_requests_pause_only_for_matching_fetch_patterns
    [Fact]
    public void ScriptedRequestsPauseOnlyForMatchingFetchPatterns()
    {
        const string url = "https://example.test/value/1";
        Assert.False(FetchOps.ShouldIntercept(false, [], url));
        Assert.True(FetchOps.ShouldIntercept(true, [], url));
        Assert.True(FetchOps.ShouldIntercept(true, ["*"], url));
        Assert.True(FetchOps.ShouldIntercept(true, ["*/value/*"], url));
        Assert.False(FetchOps.ShouldIntercept(true, ["*never-matches*"], url));
        Assert.True(FetchOps.ShouldIntercept(true, ["*never-matches*", "https://example.test/*"], url));
    }
}

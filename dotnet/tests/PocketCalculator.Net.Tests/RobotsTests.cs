namespace PocketCalculator.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in <c>robots.rs</c>.</summary>
public class RobotsTests
{
    [Fact]
    public void TestParseBasicRobots()
    {
        const string body = "User-agent: *\nDisallow: /private/\nDisallow: /admin\nAllow: /admin/public\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("example.com", body, "PocketCalculator");
        Assert.True(cache.IsAllowed("example.com", "/"));
        Assert.True(cache.IsAllowed("example.com", "/page"));
        Assert.False(cache.IsAllowed("example.com", "/private/secret"));
        Assert.False(cache.IsAllowed("example.com", "/admin"));
        Assert.True(cache.IsAllowed("example.com", "/admin/public"));
    }

    [Fact]
    public void TestNoRulesMeansAllowed()
    {
        var cache = new RobotsCache();
        Assert.True(cache.IsAllowed("unknown.com", "/anything"));
        Assert.False(cache.Contains("unknown.com"));

        cache.ParseAndStore("unknown.com", string.Empty, "PocketCalculator");
        Assert.True(cache.Contains("unknown.com"));
        Assert.True(cache.IsAllowed("unknown.com", "/anything"));
    }

    [Fact]
    public void TestDisallowAll()
    {
        const string body = "User-agent: *\nDisallow: /\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("blocked.com", body, "PocketCalculator");
        Assert.False(cache.IsAllowed("blocked.com", "/"));
        Assert.False(cache.IsAllowed("blocked.com", "/page"));
    }

    // upstream b3475a5: agent_groups_match_product_tokens_not_substrings
    [Fact]
    public void AgentGroupsMatchProductTokensNotSubstrings()
    {
        const string chrome = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
        var cache = new RobotsCache();
        cache.ParseAndStore("a.test", "User-agent: Moz\nDisallow: /\n", chrome);
        Assert.True(cache.IsAllowed("a.test", "/page"), "a token prefix is not a match");
        cache.ParseAndStore("b.test", "User-agent: chrome\nDisallow: /\n", chrome);
        Assert.False(cache.IsAllowed("b.test", "/page"), "an exact token still matches, case-insensitively");
        cache.ParseAndStore("c.test", "User-agent: ObscuraBot\nDisallow: /\n", "Obscura");
        Assert.True(cache.IsAllowed("c.test", "/page"), "our token being a substring of theirs is not a match");
        cache.ParseAndStore("d.test", "User-agent: Obscura\nDisallow: /\n", "Obscura/1.0 (+https://example.test)");
        Assert.False(cache.IsAllowed("d.test", "/page"), "a product token followed by a version matches");
    }

    // upstream 8dd9d48: consecutive_user_agent_lines_share_one_group
    [Fact]
    public void ConsecutiveUserAgentLinesShareOneGroup()
    {
        const string body = "User-agent: *\nUser-agent: Yandex\nDisallow: /search\nAllow: /search/about\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("google.test", body, "Obscura");
        Assert.False(cache.IsAllowed("google.test", "/search?q=test"));
        Assert.True(cache.IsAllowed("google.test", "/search/about"));
    }

    // upstream 8dd9d48: longest_matching_rule_wins_and_allow_wins_a_tie
    [Fact]
    public void LongestMatchingRuleWinsAndAllowWinsATie()
    {
        const string body = "User-agent: *\nAllow: /p\nDisallow: /p/private\nDisallow: /tie\nAllow: /tie\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("paths.test", body, "Obscura");
        Assert.True(cache.IsAllowed("paths.test", "/p"));
        Assert.False(cache.IsAllowed("paths.test", "/p/private/x"));
        Assert.True(cache.IsAllowed("paths.test", "/tie"));
    }

    [Fact]
    public void AMatchingSpecificGroupOverridesTheStarGroup()
    {
        const string body = "User-agent: *\nDisallow: /\n\nUser-agent: Obscura\nDisallow: /only\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("specific.test", body, "Obscura");
        Assert.True(cache.IsAllowed("specific.test", "/elsewhere"));
        Assert.False(cache.IsAllowed("specific.test", "/only"));
    }

    // upstream 9e0b8b6: unrelated_agents_in_wildcard_groups_do_not_select_specific_rules
    [Fact]
    public void UnrelatedAgentsInWildcardGroupsDoNotSelectSpecificRules()
    {
        var cache = new RobotsCache();
        const string mixed = "User-agent: *\nUser-agent: OtherBot\nDisallow: /mixed\n\n";
        cache.ParseAndStore("wildcard.test", mixed + "User-agent: *\nDisallow: /fallback\n", "Obscura");
        Assert.False(cache.IsAllowed("wildcard.test", "/mixed"));
        Assert.False(cache.IsAllowed("wildcard.test", "/fallback"));
        cache.ParseAndStore("specific.test", mixed + "User-agent: Obscura\nDisallow: /own\n", "Obscura");
        Assert.True(cache.IsAllowed("specific.test", "/mixed"));
        Assert.False(cache.IsAllowed("specific.test", "/own"));
    }

    // upstream 0e17221: cache_evicts_oldest_origin_past_the_entry_cap
    [Fact]
    public void CacheEvictsOldestOriginPastTheEntryCap()
    {
        var cache = new RobotsCache();
        for (var i = 0; i < RobotsCache.MaxRobotsEntries + 10; i++)
        {
            cache.ParseAndStore($"https://site{i}.test", string.Empty, "Obscura");
        }

        Assert.Equal(RobotsCache.MaxRobotsEntries, cache.Count);
        Assert.False(cache.Contains("https://site0.test"));
        var last = $"https://site{RobotsCache.MaxRobotsEntries + 9}.test";
        Assert.True(cache.Contains(last));
        cache.ParseAndStore(last, string.Empty, "Obscura");
        Assert.Equal(RobotsCache.MaxRobotsEntries, cache.Count);
    }

    // upstream 0e17221: rules_past_the_body_cap_are_ignored
    [Fact]
    public void RulesPastTheBodyCapAreIgnored()
    {
        var body = new System.Text.StringBuilder("User-agent: *\nDisallow: /early\n");
        while (body.Length <= RobotsCache.MaxRobotsBodyBytes)
        {
            body.Append("# padding padding padding padding padding\n");
        }

        body.Append("Disallow: /late\n");
        var cache = new RobotsCache();
        cache.ParseAndStore("big.test", body.ToString(), "Obscura");
        Assert.False(cache.IsAllowed("big.test", "/early"));
        Assert.True(cache.IsAllowed("big.test", "/late"));
    }
}

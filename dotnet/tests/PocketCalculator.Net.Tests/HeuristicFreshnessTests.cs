using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Net.Tests;

// Port of upstream a134a7e's resource_cache_tests and ssrf_tests additions (client.rs).
public class HeuristicFreshnessTests
{
    private static Response TestResponse(int status, params (string Name, string Value)[] headers) => new()
    {
        Url = new Uri("http://example.test/app.js"),
        Status = status,
        Headers = headers.ToDictionary(h => h.Name, h => h.Value),
        Body = [1],
        RedirectedFrom = [],
    };

    private static long Fixed(string date) =>
        CookieJar.TryParseHttpDate(date, out var seconds) ? seconds : throw new InvalidOperationException(date);

    private static readonly long FixtureNow = Fixed("Fri, 06 Nov 2026 08:59:06 GMT");

    // Receive the dated fixtures at their Date, independent of the wall clock.
    private static TimeSpan? AtFixtureTime(Response response) =>
        PocketCalculatorHttpClient.ResponseCacheLifetime(response, TimeSpan.Zero, () => FixtureNow);

    [Fact]
    public void Last_modified_without_explicit_expiry_gets_a_tenth_of_its_age()
    {
        // 334906 s since Last-Modified: a tenth is 33490 s.
        var response = TestResponse(200,
            ("date", "Fri, 06 Nov 2026 08:59:06 GMT"),
            ("last-modified", "Mon, 02 Nov 2026 11:57:20 GMT"));
        Assert.Equal(TimeSpan.FromSeconds(33490), AtFixtureTime(response));
    }

    [Fact]
    public void Heuristic_lifetime_is_capped_and_respects_explicit_directives()
    {
        (string, string) date = ("date", "Fri, 06 Nov 2026 08:59:06 GMT");
        (string, string) oldModified = ("last-modified", "Mon, 01 Jan 2018 00:00:00 GMT");
        var cap = PocketCalculatorHttpClient.HeuristicFreshnessCap;
        Assert.Equal(cap, AtFixtureTime(TestResponse(200, date, oldModified)));
        // A directive without max-age (public, private) still allows the heuristic.
        Assert.Equal(cap, AtFixtureTime(TestResponse(200, date, oldModified, ("cache-control", "public"))));
        (string, string)[][] blocked =
        [
            [date, oldModified, ("cache-control", "no-cache")],
            [date, oldModified, ("cache-control", "no-store")],
            [date, oldModified, ("cache-control", "max-age=0")],
            [date, oldModified, ("expires", "Fri, 06 Nov 2026 09:00:00 GMT")],
            [date, oldModified, ("set-cookie", "a=1")],
            [date, ("last-modified", "Fri, 06 Nov 2026 08:59:06 GMT")],
            [date, ("last-modified", "Sat, 07 Nov 2026 00:00:00 GMT")],
            [date, ("last-modified", "not a date")],
            [date],
        ];
        foreach (var headers in blocked)
        {
            Assert.Null(AtFixtureTime(TestResponse(200, headers)));
        }
        Assert.Null(AtFixtureTime(TestResponse(201, date, oldModified)));
        Assert.Null(AtFixtureTime(TestResponse(404, date, oldModified)));
        // Explicit max-age still wins over the heuristic.
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            AtFixtureTime(TestResponse(200, date, oldModified, ("cache-control", "max-age=60"))));
    }

    [Theory]
    [InlineData("max-age=invalid")]
    [InlineData("max-age=-1")]
    [InlineData("max-age=18446744073709551616")]
    [InlineData("max-age")]
    [InlineData("max-age=")]
    [InlineData("max-age = 0")]
    [InlineData("max-age=\"0\"")]
    [InlineData("max-age=0, max-age=60")]
    [InlineData("max-age=60, max-age=invalid")]
    [InlineData("no-cache=\"X-Version\"")]
    [InlineData("No-Cache = \"X-Version, X-Other\"")]
    public void Explicit_cache_directives_do_not_fall_back_to_heuristic_freshness(string directive)
    {
        var response = TestResponse(200,
            ("last-modified", "Mon, 01 Jan 2018 00:00:00 GMT"),
            ("cache-control", directive));
        Assert.Null(PocketCalculatorHttpClient.ResponseCacheLifetime(response, TimeSpan.Zero));
    }

    [Fact]
    public void Already_stale_heuristic_responses_are_not_cached()
    {
        const string old = "Mon, 01 Jan 2018 00:00:00 GMT";
        (string, string)[][] cases =
        [
            [("last-modified", old), ("age", "86400")],
            [("last-modified", old), ("age", "18446744073709551616")],
            [("date", "Tue, 02 Jan 2018 00:00:00 GMT"), ("last-modified", old)],
            [("date", "Tuesday, 02-Jan-18 00:00:00 GMT"), ("last-modified", old)],
            [("date", "Tue Jan  2 00:00:00 2018"), ("last-modified", old)],
        ];
        foreach (var headers in cases)
        {
            Assert.Null(PocketCalculatorHttpClient.ResponseCacheLifetime(TestResponse(200, headers), TimeSpan.Zero));
        }
    }

    [Fact]
    public void Remaining_heuristic_freshness_accounts_for_age_and_transport_delay()
    {
        var response = TestResponse(200,
            ("date", "Fri, 06 Nov 2026 08:59:06 GMT"),
            ("last-modified", "Mon, 02 Nov 2026 11:57:20 GMT"),
            ("age", "1000"));
        var delay = TimeSpan.FromMilliseconds(200);
        Assert.Equal(
            TimeSpan.FromSeconds(32490) - delay,
            PocketCalculatorHttpClient.ResponseCacheLifetime(response, delay, () => FixtureNow));
        Assert.Equal(
            TimeSpan.FromSeconds(31490),
            PocketCalculatorHttpClient.ResponseCacheLifetime(response, delay, () => FixtureNow + 2000));
        Assert.Null(PocketCalculatorHttpClient.ResponseCacheLifetime(response, TimeSpan.FromSeconds(33490), () => FixtureNow));
    }
}

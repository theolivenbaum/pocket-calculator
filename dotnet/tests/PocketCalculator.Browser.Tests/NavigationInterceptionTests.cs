using System.Text;
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// upstream b76d966 (the <c>navigation_interception_*</c> tests in <c>page.rs</c>): an
/// HTTP(S) document request pauses on the interception channel before the transport.
/// </summary>
public sealed class NavigationInterceptionTests
{
    private static async Task<InterceptedRequest> NextRequestAsync(
        System.Threading.Channels.ChannelReader<InterceptedRequest> requests)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await requests.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task NavigationInterceptionFulfillsBeforeTransportAndPreservesBytes()
    {
        using Page page = PageFixtures.NewPage("document-interception");
        var requests = page.EnableInterception();
        byte[] bytes = Encoding.UTF8.GetBytes("<!doctype html><title>Fulfilled</title><p>replacement</p>");
        Task navigation = page.NavigateAsync("http://127.0.0.1:9/original");
        InterceptedRequest request = await NextRequestAsync(requests);
        Assert.Equal("Document", request.ResourceType);
        Assert.Equal("GET", request.Method);
        Assert.Equal(page.Id, request.PageId);
        Assert.StartsWith(page.Id + ".", request.RequestId, StringComparison.Ordinal);
        request.Resolver.TrySetResult(new InterceptResolution.Fulfill(
            201,
            new Dictionary<string, string>
            {
                ["content-type"] = "text/html",
                ["set-cookie"] = "first=1; Path=/\nsecond=2; Path=/",
            },
            "not the decoded body",
            bytes));
        await navigation;

        Assert.Equal("Fulfilled", page.Title);
        NetworkEvent document = Assert.Single(page.NetworkEvents, e => e.ResourceType == "Document");
        Assert.Equal(201, document.Status);
        Assert.True(document.Intercepted);
        Assert.Equal(request.RequestId, document.RequestId);
        Assert.Equal(Encoding.UTF8.GetString(bytes), page.GetResponseBody(document.RequestId)!.CdpBody());
        string[] setCookies = document.ResponseHeaders["set-cookie"].Split('\n');
        Array.Sort(setCookies, StringComparer.Ordinal);
        Assert.Equal(["first=1; Path=/", "second=2; Path=/"], setCookies);
        string cookies = page.Context.CookieJar.GetCookieHeader(new Uri("http://127.0.0.1:9/original"));
        Assert.Contains("first=1", cookies.Split("; "));
        Assert.Contains("second=2", cookies.Split("; "));
    }

    [Fact]
    public async Task NavigationInterceptionContinuesWithRequestLocalOverrides()
    {
        using var server = TestHttpServer.Start(_ => TestResponse.Html("<title>continued</title>"));
        using Page page = PageFixtures.NewPage("continued-navigation");
        var requests = page.EnableInterception();
        Task navigation = page.NavigateAsync("http://127.0.0.1:9/original");
        InterceptedRequest request = await NextRequestAsync(requests);
        request.Resolver.TrySetResult(new InterceptResolution.Continue(
            server.Origin + "/changed",
            "POST",
            new Dictionary<string, string>
            {
                ["content-type"] = "application/octet-stream",
                ["x-navigation"] = "isolated",
            },
            [0, 255, 128]));
        await navigation;

        TestRequest sent = Assert.Single(server.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/changed", sent.Path);
        string head = sent.Headers.ToLowerInvariant();
        Assert.Contains("content-type: application/octet-stream", head, StringComparison.Ordinal);
        Assert.DoesNotContain("x-www-form-urlencoded", head, StringComparison.Ordinal);
        Assert.Contains("x-navigation: isolated", head, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0, 255, 128 }, sent.Body);
        Assert.Equal("continued", page.Title);
        // A network rewrite does not rewrite the document URL.
        Assert.Equal("http://127.0.0.1:9/original", page.UrlString());
        // Per-request headers cannot leak to another request.
        Assert.Empty(page.HttpClient.ExtraHeaders);
        Assert.True(Assert.Single(page.NetworkEvents, e => e.ResourceType == "Document").Intercepted);
    }

    [Fact]
    public async Task NavigationInterceptionCanFailWithoutContactingTransport()
    {
        using Page page = PageFixtures.NewPage("aborted-navigation");
        var requests = page.EnableInterception();
        Task navigation = page.NavigateAsync("http://127.0.0.1:9/aborted");
        InterceptedRequest request = await NextRequestAsync(requests);
        request.Resolver.TrySetResult(new InterceptResolution.Fail("Aborted"));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => navigation);
        Assert.Contains("ERR_ABORTED", error.Message, StringComparison.Ordinal);
        Assert.Equal(LifecycleState.Failed, page.Lifecycle);
    }

    [Fact]
    public async Task NavigationInterceptionCannotChangeTheScheme()
    {
        using Page page = PageFixtures.NewPage("scheme-navigation");
        var requests = page.EnableInterception();
        Task navigation = page.NavigateAsync("http://127.0.0.1:9/start");
        InterceptedRequest request = await NextRequestAsync(requests);
        request.Resolver.TrySetResult(new InterceptResolution.Continue("file:///etc/passwd", null, null, null));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => navigation);
        Assert.Contains("URL scheme", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyRequestsMatchingThePatternsPause()
    {
        using var server = TestHttpServer.Start(_ => TestResponse.Html("<title>direct</title>"));
        using Page page = PageFixtures.NewPage("unmatched-navigation");
        page.InterceptBlockPatterns.Add("*://example.test/*");
        var requests = page.EnableInterception();
        await page.NavigateAsync(server.Origin + "/");
        Assert.Equal("direct", page.Title);
        Assert.False(requests.TryRead(out _));
        Assert.False(Assert.Single(page.NetworkEvents, e => e.ResourceType == "Document").Intercepted);
    }

    [Fact]
    public void ErrorReasonsBecomeChromiumErrorNames()
    {
        Assert.Equal("net::ERR_ABORTED", Page.NetErrorName("Aborted"));
        Assert.Equal("net::ERR_CONNECTION_REFUSED", Page.NetErrorName("ConnectionRefused"));
        Assert.Equal("net::ERR_BLOCKED_BY_CLIENT", Page.NetErrorName("BlockedByClient"));
    }
}

using Xunit;

namespace PocketCalculator.Browser.Tests;

// Upstream 1e4238e (#1500): replacing the global Storage constructor must not break
// localStorage or sessionStorage. Chromium 1243 answers ["1","2",2,true,"3","function"].
public sealed class StorageConstructorReplaced
{
    [Fact]
    public async Task Storage_stays_usable_after_its_global_constructor_is_replaced()
    {
        using var server = TestHttpServer.Start(_ => TestResponse.Html("<p>x</p>"));
        using Page page = PageFixtures.NewPage("storage-ctor");
        await page.NavigateAsync($"{server.Origin}/");
        var result = page.Evaluate(
            "(() => { window.Storage = {}; localStorage.setItem('a', '1'); localStorage.b = '2'; sessionStorage.setItem('c', '3');"
            + " return JSON.stringify([localStorage.getItem('a'), localStorage.b, localStorage.length, 'a' in localStorage, sessionStorage.c, typeof localStorage.getItem]); })()");
        Assert.Equal("[\"1\",\"2\",2,true,\"3\",\"function\"]", result?.GetValue<string>());
    }
}

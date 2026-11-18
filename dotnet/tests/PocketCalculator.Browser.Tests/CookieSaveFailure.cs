using Xunit;

namespace PocketCalculator.Browser.Tests;

// Upstream 1b259b9: a cookie save that fails is reported, not swallowed.
public class CookieSaveFailure
{
    [Fact]
    public void A_failed_cookie_save_is_reported()
    {
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var blocker = Path.Combine(directory.FullName, "not-a-dir");
            File.WriteAllText(blocker, "x");
            var context = BrowserContext.WithStorage("save-fail", Path.Combine(blocker, "profile"));
            context.CookieJar.SetCookie("session=abc123; Path=/", new Uri("https://example.com/"));

            var error = context.SaveCookies();

            Assert.NotNull(error);
            Assert.StartsWith($"Could not save cookies to {Path.Combine(blocker, "profile", "cookies.json")}: ", error);
            Assert.EndsWith("They will not be persisted, so the next run starts without them.", error);

            var ok = BrowserContext.WithStorage("save-ok", Path.Combine(directory.FullName, "profile"));
            Assert.Null(ok.SaveCookies());
            Assert.True(File.Exists(Path.Combine(directory.FullName, "profile", "cookies.json")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

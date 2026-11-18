using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// Upstream c7ec60a <c>color_scheme_reaches_existing_and_new_child_documents</c>. Upstream
/// reaches the child through <c>iframe.contentWindow</c> from the parent; the port's parent
/// realm cannot hold a frame realm's objects, so the same assertions run inside the frame.
/// </summary>
public sealed class MediaEmulationFrameTests
{
    private const string Child =
        "<html><body><script>globalThis.q = matchMedia('(prefers-color-scheme: dark)');"
        + " globalThis.changes = []; q.onchange = e => changes.push(e.matches);</script></body></html>";

    [Fact]
    public async Task ColorSchemeReachesExistingAndNewChildDocuments()
    {
        using TestHttpServer server = TestHttpServer.Start(request => TestResponse.Html(request.Path switch
        {
            "/child.html" => Child,
            _ => "<html><body><iframe src=\"/child.html\"></iframe></body></html>",
        }));
        using Page page = PageFixtures.FramePage("media-frames");
        await page.NavigateAsync($"{server.Origin}/");
        await page.SettleAsync(1_000);
        Assert.Single(page.Frames);

        page.SetMediaPreferences(true, true);
        await page.SettleAsync(200);
        PageFixtures.AssertJson("[true, [true]]", page.EvaluateInFrame(0, "[q.matches, changes]"));

        page.Js!.Evaluate(
            "(function(){ const f = document.createElement('iframe'); f.src = '/child.html'; document.body.appendChild(f); return 1; })()");
        await page.SettleAsync(1_000);
        Assert.Equal(2, page.Frames.Count);
        PageFixtures.AssertJson(
            "true",
            page.EvaluateInFrame(1, "matchMedia('(prefers-color-scheme: dark) and (prefers-reduced-motion: reduce)').matches"));

        page.SetMediaPreferences(false, false);
        await page.SettleAsync(200);
        PageFixtures.AssertJson("[false, [true, false]]", page.EvaluateInFrame(0, "[q.matches, changes]"));
        PageFixtures.AssertJson("[false, [false]]", page.EvaluateInFrame(1, "[q.matches, changes]"));
    }
}

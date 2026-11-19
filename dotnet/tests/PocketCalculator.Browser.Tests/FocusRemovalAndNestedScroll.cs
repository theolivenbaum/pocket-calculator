using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// Ports of upstream c714042's runtime.rs tests, with Chromium 1243's values: removing the
/// focused element's subtree resets focus to the body and fires blur and focusout (upstream
/// expects no events), and scrollIntoView moves nested scrollports before the viewport.
/// </summary>
public sealed class FocusRemovalAndNestedScroll
{
    private static async Task<string?> RunAsync(string html, string script)
    {
        using var server = TestHttpServer.Start(_ => TestResponse.Html(html));
        using Page page = PageFixtures.NewPage("c714042");
        page.SetViewport((800, 600));
        await page.NavigateAsync($"{server.Origin}/");
        return page.Evaluate(script)?.GetValue<string>();
    }

    [Fact]
    public async Task Scroll_into_view_moves_nested_scrollports_before_the_viewport()
    {
        string? result = await RunAsync(
            """
            <html style="margin:0"><body style="margin:0;height:1800px">
                <div id="scroller" style="margin-top:200px;height:200px;overflow:auto">
                    <div style="height:600px"></div>
                    <div id="target" style="height:40px"></div>
                    <div style="height:600px"></div>
                </div>
            </body></html>
            """,
            """
            (() => {
                const scroller = document.getElementById('scroller');
                const target = document.getElementById('target');
                const results = [];
                for (const block of ['nearest', 'center', 'start', 'end']) {
                    scrollTo(0, 0);
                    scroller.scrollTop = 0;
                    target.scrollIntoView({block});
                    results.push([scroller.scrollTop, scrollY, target.getBoundingClientRect().top]);
                }
                scrollTo(0, 0);
                target.scrollIntoView({block: 'nearest'});
                results.push([scroller.scrollTop, scrollY, target.getBoundingClientRect().top]);
                return JSON.stringify(results);
            })()
            """);
        Assert.Equal("[[440,0,360],[520,0,280],[600,200,0],[440,0,360],[440,0,360]]", result);
    }

    [Fact]
    public async Task Removing_a_focused_subtree_resets_focus_and_fires_blur()
    {
        string? result = await RunAsync(
            "<main id=parent></main>",
            """
            (() => {
                const parent = document.getElementById('parent');
                const rows = [];
                for (const remove of [
                    host => host.remove(),
                    host => parent.removeChild(host),
                    host => parent.replaceChild(document.createElement('div'), host),
                    host => { parent.innerHTML = ''; },
                    host => { parent.textContent = ''; },
                    host => parent.replaceChildren(),
                    host => host.replaceWith(document.createElement('div')),
                ]) {
                    parent.innerHTML = '<section><button>Apply</button></section>';
                    const host = parent.firstChild, button = host.firstChild;
                    let blurred = 0;
                    button.addEventListener('blur', () => blurred++);
                    button.addEventListener('focusout', () => blurred++);
                    button.focus();
                    const focused = document.activeElement === button;
                    remove(host);
                    parent.appendChild(host);
                    rows.push([focused, document.activeElement === document.body, blurred]);
                }
                return JSON.stringify(rows);
            })()
            """);
        Assert.Equal(string.Concat(Enumerable.Repeat("[true,true,2],", 7)).TrimEnd(',') is var rows ? $"[{rows}]" : null, result);
    }
}

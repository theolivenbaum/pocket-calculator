using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Port of crates/obscura-js/tests/media_query_events.rs (upstream c9978c3, 6ba3613):
/// MediaQueryList change delivery and its onchange event handler.
/// </summary>
public sealed class MediaQueryEventsTests
{
    private static string Json(PocketCalculatorJsRuntime rt, string expression) =>
        rt.Evaluate($"JSON.stringify({expression})")!.GetValue<string>();

    [Fact]
    public async Task MediaQueryListDeliversChangesAndHonorsListenerLifecycle()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<body></body>"));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();
        rt.Evaluate(
            """
            (() => {
              globalThis.events = [];
              globalThis.q = matchMedia('(min-width: 1200px)');
              globalThis.legacy = e => events.push(['legacy', e.matches, e.target === q]);
              q.addListener(legacy);
              q.addListener(legacy);
              q.addEventListener('change', e => events.push(['once', e.matches]), {once: true});
              q.onchange = e => events.push(['property', e.matches]);
              const ac = new AbortController();
              q.addEventListener('change', () => events.push(['aborted']), {signal: ac.signal});
              ac.abort();
              return null;
            })()
            """);
        rt.SetViewport(1000.0, 700.0);
        Assert.Equal("[false,0]", Json(rt, "[q.matches, events.length]"));
        await rt.RunEventLoopBoundedAsync(30);
        rt.Evaluate("(() => { q.removeListener(legacy); q.onchange = null; })()");
        rt.SetViewport(1280.0, 720.0);
        await rt.RunEventLoopBoundedAsync(30);
        Assert.Equal("""[["legacy",false,true],["once",false],["property",false]]""", Json(rt, "events"));
        Assert.Equal("[true,true,true]", Json(rt, "[q instanceof MediaQueryList, q instanceof EventTarget, q.matches]"));
        Assert.Equal(
            "\"TypeError\"",
            Json(rt, "(() => { try { new MediaQueryList(); } catch(e) { return e.name; } })()"));
    }

    [Fact]
    public void MediaQueryPropertyHandlerIsIndependentAndKeepsListenerOrder()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<body></body>"));
        rt.RunPageInit();
        string result = Json(rt,
            """
            (() => {
              const q = matchMedia('(min-width: 1px)'), seen = [], results = [];
              const shared = () => seen.push('shared');
              const emit = () => {
                seen.length = 0;
                q.dispatchEvent(new MediaQueryListEvent('change'));
                results.push(seen.slice());
              };
              q.addEventListener('change', shared);
              q.onchange = shared;
              q.addEventListener('change', () => seen.push('last'));
              emit();
              q.onchange = () => seen.push('replacement');
              emit();
              q.onchange = null;
              emit();
              q.onchange = shared;
              emit();
              q.removeEventListener('change', shared);
              emit();
              return results;
            })()
            """);
        Assert.Equal(
            """[["shared","shared","last"],["shared","replacement","last"],["shared","last"],["shared","last","shared"],["last","shared"]]""",
            result);
    }

    /// <summary>
    /// Port of crates/obscura-js/tests/reduced_motion.rs (upstream c9978c3).
    /// </summary>
    [Fact]
    public async Task ReducedMotionUpdatesQueriesAndChangeEvents()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<body></body>"));
        rt.RunPageInit();
        rt.Evaluate(
            "(() => { globalThis.q = matchMedia('(prefers-reduced-motion: reduce)'); globalThis.changes = []; q.onchange = e => changes.push(e.matches); })()");
        rt.SetMediaPreferences(true, false);
        await rt.RunEventLoopBoundedAsync(30);
        rt.SetMediaPreferences(false, false);
        await rt.RunEventLoopBoundedAsync(30);
        Assert.Equal("[false,[true,false]]", Json(rt, "[q.matches, changes]"));
    }

    [Fact]
    public void ReducedMotionInvalidatesCssGeometry()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<body></body>"));
        rt.RunPageInit();
        rt.Evaluate(
            """
            (() => {
              const html = '<style>div{width:100px}@media(prefers-reduced-motion:reduce){div{width:40px}}</style><div>probe</div>';
              document.body.innerHTML = html;
              globalThis.widths = () => [document.querySelector('div').getBoundingClientRect().width];
            })()
            """);
        Assert.Equal("[100]", Json(rt, "widths()"));
        rt.SetMediaPreferences(true, false);
        Assert.Equal("[40]", Json(rt, "widths()"));
        rt.SetMediaPreferences(false, false);
        Assert.Equal("[100]", Json(rt, "widths()"));
    }

    /// <summary>
    /// Port of crates/obscura-js/tests/color_scheme.rs (upstream c7ec60a). Every answer is
    /// Chromium 157's under the same emulation.
    /// </summary>
    [Fact]
    public async Task ColorSchemeUpdatesCompoundQueriesOnceAndClears()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><body></body>");
        var rt = fixture.Runtime;
        rt.Evaluate(
            """
            (() => {
              globalThis.q = matchMedia('(prefers-color-scheme: dark) and (prefers-reduced-motion: reduce)');
              globalThis.changes = [];
              q.addListener(e => changes.push(e.matches));
              globalThis.probe = () => ['dark', 'light', 'no-preference'].map(v =>
                matchMedia('(prefers-color-scheme: ' + v + ')').matches);
            })()
            """);
        Assert.Equal("[false,true,false]", Json(rt, "probe()"));
        Assert.Equal(
            "[true,false]",
            Json(rt, "[matchMedia('(prefers-color-scheme)').matches, matchMedia('(prefers-color-scheme:invalid)').matches]"));
        rt.SetMediaPreferences(true, true);
        await rt.RunEventLoopBoundedAsync(30);
        rt.SetMediaPreferences(true, true);
        await rt.RunEventLoopBoundedAsync(30);
        Assert.Equal("[[true,false,false],[true]]", Json(rt, "[probe(), changes]"));
        rt.SetMediaPreferences(false, true);
        Assert.Equal("[true,false,false]", Json(rt, "probe()"));
        rt.SetMediaPreferences(false, false);
        await rt.RunEventLoopBoundedAsync(30);
        Assert.Equal("[[false,true,false],[true,false]]", Json(rt, "[probe(), changes]"));
    }

    [Fact]
    public void ColorSchemeInvalidatesLiveStylesGeometryAndPrintSelection()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><body></body>");
        var rt = fixture.Runtime;
        rt.Evaluate(
            """
            (() => {
              document.body.innerHTML = '<style>div{width:100px;color:rgb(1,2,3)}@media(prefers-color-scheme:dark){div{width:40px;color:rgb(4,5,6)}}@media print and (prefers-color-scheme:dark){div{width:60px}}</style><div>probe</div>';
              globalThis.box = document.querySelector('div'); globalThis.live = getComputedStyle(box);
              globalThis.probe = () => [live.color, live.width, box.getBoundingClientRect().width];
            })()
            """);
        Assert.Equal("""["rgb(1, 2, 3)","100px",100]""", Json(rt, "probe()"));
        rt.SetMediaPreferences(true, true);
        Assert.Equal("""["rgb(4, 5, 6)","40px",40]""", Json(rt, "probe()"));
        var previous = rt.SetRenderMedia(
            PocketCalculator.Render.Css.CssMediaType.Print
            | PocketCalculator.Render.Css.CssMediaType.ReducedMotion
            | PocketCalculator.Render.Css.CssMediaType.DarkColorScheme);
        Assert.Equal("60", Json(rt, "box.getBoundingClientRect().width"));
        rt.SetRenderMedia(previous);
        rt.SetMediaPreferences(false, false);
        Assert.Equal("""["rgb(1, 2, 3)","100px",100]""", Json(rt, "probe()"));
    }

    /// <summary>
    /// The port's addition: CDP's media type. Chromium 157 under <c>media: 'print'</c> matches
    /// print and applies <c>@media print</c>; under an unknown type it matches neither screen
    /// nor print.
    /// </summary>
    [Fact]
    public async Task EmulatedMediaTypeSelectsPrintOrNeither()
    {
        using var fixture = RuntimeFixture.Setup(
            "<!doctype html><style>div{width:100px}@media print{div{width:50px}}</style><div>probe</div>");
        var rt = fixture.Runtime;
        rt.Evaluate(
            "(() => { globalThis.changes = []; globalThis.pq = matchMedia('print'); pq.onchange = e => changes.push([e.media, e.matches]); })()");
        const string Probe =
            "[matchMedia('print').matches, matchMedia('screen').matches, matchMedia('all').matches, document.querySelector('div').getBoundingClientRect().width]";
        Assert.Equal("[false,true,true,100]", Json(rt, Probe));
        rt.SetMediaPreferences(false, false, "print");
        Assert.Equal("[true,false,true,50]", Json(rt, Probe));
        rt.SetMediaPreferences(false, false, "bogus");
        Assert.Equal("[false,false,true,100]", Json(rt, Probe));
        rt.SetMediaPreferences(false, false);
        Assert.Equal("[false,true,true,100]", Json(rt, Probe));
        await rt.RunEventLoopBoundedAsync(30);
        Assert.Equal("""[["print",true],["print",false]]""", Json(rt, "changes"));
    }
}

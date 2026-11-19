using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream 30e8d7a: offset* and client* are untransformed, so a transform-only inline-style
/// write leaves the retained layout for them and only visual geometry materialises it. Every
/// expected value was measured in Chromium (playwright chromium, a 200x100 viewport).
/// </summary>
public sealed partial class RuntimeTests
{
    // Rust transform_only_writes_retain_untransformed_cssom_box_metrics, plus a sibling's
    // client and offset values read while the transform is pending.
    [Fact]
    public void TransformOnlyWritesRetainUntransformedCssomBoxMetrics()
    {
        using var rt = new PocketCalculatorJsRuntime();
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
            <div id="box" style="width:40px;height:20px;transform:scale(2)"></div>
            <div id="parent" style="width:80px;height:20px">
            <div id="fixed" style="position:fixed;width:50%;height:5px"></div>
            </div>
            <div id="sib" style="width:30px;height:10px;border:2px solid;padding:3px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        AssertJsonEquals("[40, 40, 80]", rt.Evaluate(
            """
            (() => { const box = document.getElementById('box');
                return [box.offsetWidth, box.clientWidth, box.getBoundingClientRect().width]; })()
            """));
        AssertJsonEquals("[40, 40, 40, 36, 40, 0]", rt.Evaluate(
            """
            (() => { const box = document.getElementById('box'), sib = document.getElementById('sib');
                box.style.transform = 'scale(3)';
                return [box.offsetWidth, box.clientWidth, sib.offsetWidth, sib.clientWidth,
                    sib.offsetTop, sib.offsetLeft]; })()
            """));
        Assert.NotEmpty(rt.State.PendingStyleMutations);

        AssertJsonEquals("120", rt.Evaluate("document.getElementById('box').getBoundingClientRect().width"));
        Assert.Empty(rt.State.PendingStyleMutations);

        // A layout-affecting inline edit does not take the transform fast path.
        AssertJsonEquals("70", rt.Evaluate(
            "(() => { const box = document.getElementById('box'); box.style.width = '70px'; return box.offsetWidth; })()"));
        AssertJsonEquals("100", rt.Evaluate("document.getElementById('fixed').offsetWidth"));
        // An ancestor's transform makes it the containing block of a fixed descendant.
        AssertJsonEquals("[40, 40]", rt.Evaluate(
            """
            (() => { document.getElementById('parent').style.transform = 'translateZ(0)';
                const fixed = document.getElementById('fixed');
                return [fixed.offsetWidth, fixed.clientWidth]; })()
            """));
    }

    [Theory]
    [InlineData("width:1px;transform:none", "width:1px;transform:scale(2)", true)]
    [InlineData("transform:none", null, true)]
    [InlineData(null, "-webkit-transform:rotate(1deg)", true)]
    [InlineData("width:1px", "width:2px", false)]
    [InlineData("width:1px;height:1px", "height:1px;width:1px", false)]
    [InlineData("transform:none", "transform:none;translate:1px", false)]
    [InlineData("--x:1", "--x:2;transform:none", false)]
    public void InlineStyleChangePreservesBoxMetricsOnlyForTransforms(string? before, string? after, bool expected) =>
        Assert.Equal(
            expected,
            PocketCalculator.Render.Css.CssDeclarations.InlineStyleChangePreservesBoxMetrics(before, after));

    // Port addition: an ancestor moving from one transform to another keeps its descendants'
    // containing block and offsetParent, so they are measured without a layout; switching to or
    // from none changes both (Chromium: offsetParent becomes the transformed ancestor).
    [Fact]
    public void AncestorTransformChangesKeepDescendantMetricsUnlessTheyToggleNone()
    {
        using var rt = new PocketCalculatorJsRuntime();
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <body style="margin:0"><div id=g style="padding:7px"><div id=t style="margin:5px;padding:3px">
            <div id=in style="width:10px;height:4px;margin-left:2px"></div>
            <div id=abs style="position:absolute;left:3px;top:4px;width:50%"></div></div></div></body>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        const string Probe =
            """
            (() => { const i = document.getElementById('in'), a = document.getElementById('abs');
                return [i.offsetParent.id || i.offsetParent.tagName, i.offsetLeft, i.offsetTop, i.offsetWidth,
                    a.offsetParent.id || a.offsetParent.tagName, a.offsetLeft, a.offsetWidth]; })()
            """;
        AssertJsonEquals("""["BODY", 17, 15, 10, "BODY", 3, 100]""", rt.Evaluate(Probe));
        rt.Evaluate("document.getElementById('t').style.transform = 'translateX(5px)'");
        AssertJsonEquals("""["t", 5, 3, 10, "t", 3, 88]""", rt.Evaluate(Probe));
        Assert.Empty(rt.State.PendingStyleMutations);
        rt.Evaluate("document.getElementById('t').style.transform = 'translateX(9px)'");
        AssertJsonEquals("""["t", 5, 3, 10, "t", 3, 88]""", rt.Evaluate(Probe));
        Assert.NotEmpty(rt.State.PendingStyleMutations);
        rt.Evaluate("document.getElementById('t').style.transform = 'none'");
        AssertJsonEquals("""["BODY", 17, 15, 10, "BODY", 3, 100]""", rt.Evaluate(Probe));
    }

    [Theory]
    [InlineData("transform:translateX(1px)", "transform:translateX(2px)", true)]
    [InlineData("transform:none", "transform:none", true)]
    [InlineData(null, "color:red", true)]
    [InlineData(null, "transform:none", false)]
    [InlineData("transform:none", "transform:scale(2)", false)]
    [InlineData("transform:scale(2)", "transform:var(--t)", false)]
    [InlineData("transform:scale(2)", "transform:scale(3) !important", false)]
    [InlineData("transform:scale(2)", "transform:bogus(1)", false)]
    public void InlineTransformKeepsContainingBlockOnlyForTheSameState(string? before, string? after, bool expected) =>
        Assert.Equal(
            expected,
            PocketCalculator.Render.Css.CssDeclarations.InlineTransformKeepsContainingBlock(before, after));

    // Rust dom_events_only_reach_window_through_document (same commit; already so here).
    [Fact]
    public void DomEventsOnlyReachWindowThroughDocument()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        AssertJsonEquals(
            """["document-capture", "script", "window-click-capture", "script"]""",
            fixture.Runtime.Evaluate(
                """
                (() => { const seen = [];
                    window.addEventListener('load', () => seen.push('window-capture'), true);
                    window.addEventListener('click', () => seen.push('window-click-capture'), true);
                    document.addEventListener('load', () => seen.push('document-capture'), true);
                    document.addEventListener('load', () => seen.push('document-bubble'));
                    const script = document.createElement('script');
                    script.addEventListener('load', () => seen.push('script'));
                    document.head.appendChild(script);
                    script.dispatchEvent(new Event('load'));
                    script.dispatchEvent(new Event('click'));
                    script.remove();
                    script.dispatchEvent(new Event('load'));
                    script.dispatchEvent(new Event('click'));
                    return seen; })()
                """));
    }

    // Rust elements_have_host_object_brands_for_plain_object_guards. Upstream brands every
    // element HTMLElement/SVGElement; Chromium (and the port) give the concrete interface.
    [Fact]
    public void ElementsHaveHostObjectBrandsForPlainObjectGuards()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id='carousel'></div></body></html>");
        AssertJsonEquals(
            """["[object HTMLDivElement]", "[object SVGSVGElement]", false, true]""",
            fixture.Runtime.Evaluate(
                """
                (() => { const element = document.getElementById('carousel');
                    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                    const isPlainObject = value => typeof value === 'object' && value !== null
                        && value.constructor
                        && Object.prototype.toString.call(value).slice(8, -1) === 'Object';
                    const target = {}; const source = { element };
                    target.element = isPlainObject(source.element) ? {} : source.element;
                    return [Object.prototype.toString.call(element), Object.prototype.toString.call(svg),
                        isPlainObject(element), target.element === element]; })()
                """));
    }
}

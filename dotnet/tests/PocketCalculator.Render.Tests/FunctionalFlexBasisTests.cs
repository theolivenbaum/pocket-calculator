// upstream merge fded066 (with 3085f5a, b60536d): the flex shorthand and functional flex bases.
// Ports style.rs `functional_flex_basis_owners_follow_winning_declarations`, dom.rs
// `retained_functional_flex_basis_refreshes_font_and_viewport_context`, and layout_test.rs
// `functional_flex_basis_uses_the_final_main_axis`,
// `functional_flex_basis_follows_a_nested_flex_items_used_width` and
// `a_functional_zero_basis_preserves_intrinsic_column_content`.
using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class FunctionalFlexBasisTests
{
    private const string Initial = "flex:0 0 calc(50% - 1em)";

    [Theory]
    [InlineData("flex-basis:80px")]
    [InlineData("flex:none")]
    [InlineData("flex:auto")]
    [InlineData("flex:initial")]
    [InlineData("flex:1 1 0")]
    public void FunctionalFlexBasisOwnerFollowsAWinningReset(string reset)
    {
        LayoutStyle style = ComputedStyle.Compute("div", $"{Initial};{reset}");
        Assert.Null(style.FlexBasisCalc);
    }

    [Theory]
    [InlineData("flex-basis:bananas")]
    [InlineData("flex-basis:-20px")]
    [InlineData("flex-basis:calc(1px + bananas)")]
    [InlineData("flex:-1")]
    [InlineData("flex:0 0 bananas")]
    [InlineData("flex:0 0 calc(1px) extra")]
    [InlineData("flex:10px 20px")]
    [InlineData("flex:1 initial")]
    [InlineData("flex:0 0 inherit")]
    public void AnInvalidFlexDeclarationKeepsTheCascade(string invalid)
    {
        LayoutStyle style = ComputedStyle.Compute("div", $"{Initial};{invalid}");
        Assert.NotNull(style.FlexBasisCalc);
        ComputedStyle.SetGridCalcContext(style, FontUnits.FromEm(20f), 16f, 10f, 10f);
        Assert.Equal(280f, style.FlexBasisCalc!.Resolve(600f));
        Assert.Equal(0f, style.FlexGrow);
        Assert.Equal(0f, style.FlexShrink);
    }

    [Theory]
    [InlineData("1 initial", false)]
    [InlineData("0 0 inherit", false)]
    [InlineData("10px 20px", false)]
    [InlineData("-1", false)]
    [InlineData("200px", true)]
    [InlineData("0 0 calc(50% - 1em)", true)]
    [InlineData("1 1 min(50%, 10px)", true)]
    [InlineData("unset", true)]
    public void SupportsFollowsTheFlexParser(string value, bool supported) =>
        Assert.Equal(supported, ComputedStyle.SupportsDeclaration("flex", value));

    /// <remarks>
    /// Chromium (Playwright, 2026-11-18): <c>flex: 200px</c> is <c>1 1 200px</c>, so the item
    /// grows to the 600px container; <c>unset</c> is <c>0 1 auto</c>.
    /// </remarks>
    [Theory]
    [InlineData("flex:200px", 1f, 1f)]
    [InlineData("flex:3 3 3px;flex:200px", 1f, 1f)]
    [InlineData("flex:3 3 3px;flex:unset", 0f, 1f)]
    [InlineData("flex:3 3 3px;flex:revert", 0f, 1f)]
    public void ABasisAloneSetsGrowAndShrinkToOne(string declarations, float grow, float shrink)
    {
        LayoutStyle style = ComputedStyle.Compute("div", declarations);
        Assert.Equal(grow, style.FlexGrow);
        Assert.Equal(shrink, style.FlexShrink);
    }

    [Theory]
    [InlineData("display:flex;width:1200px", "flex-basis:calc(100% - 314px)", 886f, 20f)]
    [InlineData("display:flex;width:1200px", "flex:0 0 calc(100% - 314px)", 886f, 20f)]
    [InlineData("display:flex;width:1200px", "flex:0\t0\tcalc(100% - 314px)", 886f, 20f)]
    [InlineData("display:flex;width:1200px", "flex-basis:min(50%, 300px)", 300f, 20f)]
    [InlineData("display:flex;width:1200px", "flex-basis:max(25%, 120px)", 300f, 20f)]
    [InlineData("display:flex;width:1200px", "flex-basis:clamp(120px, 60%, 900px)", 720f, 20f)]
    [InlineData("display:flex;width:1200px", "flex-basis:calc(200px + 20px)", 220f, 20f)]
    [InlineData("display:flex;flex-direction:column;width:500px;height:800px", "flex-basis:calc(50% - 20px)", 500f, 380f)]
    [InlineData("display:flex;flex-direction:column;width:500px", "flex-basis:calc(50% - 20px);height:90px", 500f, 20f)]
    [InlineData("display:flex;flex-direction:column;width:500px", "flex-basis:50%;height:90px", 500f, 20f)]
    [InlineData("display:flex;flex-direction:column;width:500px", "flex-basis:auto;height:90px", 500f, 90f)]
    [InlineData("display:flex;width:1200px", "flex-basis:calc(50% - 20px);flex-basis:80px", 80f, 20f)]
    [InlineData("display:flex;width:1200px", "flex-basis:calc(50% - 20px);flex:none;width:160px", 160f, 20f)]
    [InlineData("display:flex;width:1200px", "flex:0 0 calc(50% - 20px);flex:initial;width:70px", 70f, 20f)]
    [InlineData("display:flex;width:1200px", "flex:calc(50% - 20px)", 1200f, 20f)]
    public void FunctionalFlexBasisUsesTheFinalMainAxis(string parent, string basis, float width, float height)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>body{margin:0}div{min-width:0;min-height:0}</style>"
            + $"<div id=parent style='{parent}'><div id=item style='flex-shrink:0;{basis}'>"
            + "<div style='width:40px;height:20px'></div></div></div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (1400f, 900f));
        Rect rect = layout.Rects[tree.GetElementById("item")!.Value];
        Assert.True(
            MathF.Abs(rect.Width - width) < 0.01f && MathF.Abs(rect.Height - height) < 0.01f,
            $"parent={parent}, basis={basis}: {rect}, expected=({width}, {height})");

        // upstream 3155d6a: a column container's own height takes the content contribution.
        if (parent.Contains("flex-direction:column", StringComparison.Ordinal)
            && !parent.Contains("height:", StringComparison.Ordinal))
        {
            Assert.Equal(height, layout.Rects[tree.GetElementById("parent")!.Value].Height);
        }
    }

    /// <remarks>upstream a04e024.</remarks>
    [Theory]
    [InlineData("300px")]
    [InlineData("calc(300px)")]
    [InlineData("calc(200px + 100px)")]
    [InlineData("min(300px, 400px)")]
    public void FunctionalFlexBasisPreservesFixedIntrinsicRows(string basis)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>html,body{margin:0}#outer{display:flex;width:768px}"
            + "#parent{flex:0 0 auto;width:auto}#item{display:flex;width:100%}"
            + $"#cell{{flex:0 0 {basis};height:20px}}</style>"
            + "<div id=outer><div id=parent><div><div id=item><div id=cell></div></div></div></div></div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (800f, 600f));
        foreach (string id in new[] { "parent", "item", "cell" })
        {
            Rect rect = layout.Rects[tree.GetElementById(id)!.Value];
            Assert.True(rect.Width == 300f && rect.Height == 20f, $"{basis};{id}: {rect}");
        }
    }

    /// <remarks>upstream 3155d6a.</remarks>
    [Theory]
    [InlineData("50%")]
    [InlineData("calc(50% - 10px)")]
    [InlineData("auto")]
    public void FunctionalFlexBasisPreservesContentAspectRatioTransfer(string basis)
    {
        foreach ((string width, string height, float expectedCross) in new[]
        {
            ("width:100px", "", 100f),
            ("width:100px", "height:90px", 100f),
            ("", "height:90px", 300f),
        })
        {
            DomTree tree = HtmlParsing.ParseHtml(
                "<!doctype html><style>body{margin:0}div{min-width:0;min-height:0}</style>"
                + "<div id=parent style='display:flex;flex-direction:column;width:300px'>"
                + $"<div id=item style='flex:0 0 {basis};aspect-ratio:2;{width};{height}'></div></div>");
            DomLayout layout = RenderDom.LayoutDom(tree, (800f, 600f));
            Rect rect = layout.Rects[tree.GetElementById("item")!.Value];
            float expectedMain = basis == "auto" && height.Length != 0 ? 90f : expectedCross / 2f;
            Assert.True(rect.Width == expectedCross && rect.Height == expectedMain, $"{basis};{width};{height}: {rect}");
            Assert.True(
                layout.Rects[tree.GetElementById("parent")!.Value].Height == expectedMain,
                $"container {basis};{width};{height}: {layout.Rects[tree.GetElementById("parent")!.Value]}");
        }
    }

    /// <remarks>upstream 3155d6a.</remarks>
    [Theory]
    [InlineData("50%", 0.5f, 0f)]
    [InlineData("calc(50% - 10px)", 0.5f, 10f)]
    [InlineData("auto", 1f, 0f)]
    public void FunctionalFlexBasisResolvesAfterIntrinsicRowRatioSizing(string basis, float fraction, float offset)
    {
        foreach ((string width, float expectedParent) in new[] { ("width:90px", 90f), ("", 200f) })
        {
            DomTree tree = HtmlParsing.ParseHtml(
                "<!doctype html><style>body{margin:0}div{min-width:0;min-height:0}</style>"
                + "<div id=parent style='display:inline-flex'>"
                + $"<div id=item style='height:100px;aspect-ratio:2;flex:0 0 {basis};{width}'></div></div>");
            DomLayout layout = RenderDom.LayoutDom(tree, (800f, 600f));
            Rect rect = layout.Rects[tree.GetElementById("item")!.Value];
            Rect parentRect = layout.Rects[tree.GetElementById("parent")!.Value];
            Assert.True(parentRect.Width == expectedParent, $"{basis};{width}: parent {parentRect}");
            Assert.True(
                rect.Width == (expectedParent * fraction) - offset && rect.Height == 100f,
                $"{basis};{width}: {rect}");
        }
    }

    [Fact]
    public void FunctionalFlexBasisFollowsANestedFlexItemsUsedWidth()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>body{margin:0}div{min-width:0;min-height:0}</style>"
            + "<div style='display:flex;width:1200px'><div style='flex:0 0 300px'></div><div style='flex:1'>"
            + "<div style='display:flex;width:100%'><div id=item style='flex:0 0 calc(100% - 20px);height:20px'>"
            + "</div></div></div></div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (1400f, 900f));
        Rect rect = layout.Rects[tree.GetElementById("item")!.Value];
        Assert.True(MathF.Abs(rect.Width - 880f) < 0.01f, $"{rect}");
    }

    [Theory]
    [InlineData("0px")]
    [InlineData("calc(0px)")]
    public void AFunctionalZeroBasisPreservesIntrinsicColumnContent(string basis)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>body{margin:0}</style><div id=parent style='display:flex;"
            + $"flex-direction:column;width:500px'><div id=item style='flex:1 1 {basis};overflow:auto'>"
            + "<div style='height:100px'></div></div></div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (1400f, 900f));
        Assert.Equal(100f, layout.Rects[tree.GetElementById("item")!.Value].Height);
    }

    [Fact]
    public void RetainedFunctionalFlexBasisRefreshesFontAndViewportContext()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>body{margin:0}#parent{display:flex;width:100%;font-size:20px}"
            + "#item{flex:0 0 calc(50% - 1em + 10vw);height:20px}</style><div id=parent><div id=item></div></div>");
        NodeId parent = tree.GetElementById("parent")!.Value;
        NodeId item = tree.GetElementById("item")!.Value;
        StylesheetCache cache = new();
        Dictionary<NodeId, ReplacedIntrinsic> none = [];
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(tree, (400f, 200f), none, [], cache);
        Assert.Equal(220f, initial.Rects[item].Width);
        GridCalcExpression oldOwner = initial.Styles[item].FlexBasisCalc!;

        tree.GetNode(parent)!.SetAttribute("style", "font-size:40px");
        (DomLayout changed, _) = RenderDom.LayoutDomWithWebFontsPassLimit(
            tree, (400f, 200f), none, [], null, cache, initial.TakeRetainedStyleMaps(),
            [RetainedStyleMutation.From(new AttributeStyleMutation(parent, "style", null, "font-size:40px"))]);
        Assert.Equal(200f, changed.Rects[item].Width);
        Assert.NotSame(oldOwner, changed.Styles[item].FlexBasisCalc);

        (DomLayout resized, _) = RenderDom.LayoutDomWithWebFontsPassLimit(
            tree, (800f, 200f), none, [], null, cache, changed.TakeRetainedStyleMaps(), []);
        Assert.Equal(440f, resized.Rects[item].Width);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 200f));
        Assert.Equal(full.Rects[item], resized.Rects[item]);
    }
}

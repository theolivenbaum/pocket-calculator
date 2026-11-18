// upstream 6a7bbf3 (#764) and merge 97f8f81: a display:none float generates no box, so it must
// not change how its parent is laid out. Ports paint.rs
// `a_hidden_float_does_not_widen_a_shrink_to_fit_parent` (the merge's extended version).
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

public sealed class HiddenFloatTests
{
    private static float Width(string inner)
    {
        DomTree tree = HtmlParsing.ParseHtml($"""
            <body style="margin:0;font:14px Arial,sans-serif">
            <style>.ib{"{"}display:inline-block;width:20px{"}"}</style>
            <div id="t" style="float:left">{inner}</div></body>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 600f), null, resources)
            ?? throw new InvalidOperationException("prepared render");
        return prepared.DocumentRect(tree.GetElementById("t")!.Value)!.Value.Width;
    }

    [Fact]
    public void AHiddenFloatDoesNotWidenAShrinkToFitParent()
    {
        const string icons = """<a href="#"><i class="ib"></i>CMS<i class="ib"></i></a>""";
        float baseline = Width(icons);
        string formatted = $"\n  {icons}\n  ";
        Assert.Equal(baseline, Width(formatted));
        foreach (string side in new[] { "left", "right" })
        {
            string hidden = $"""<div style="display:none;float:{side}">A much longer hidden string</div>""";
            Assert.Equal(baseline, Width(icons + hidden));
            // Indentation exposed the wrong float path even when compact markup did not.
            Assert.Equal(baseline, Width(formatted + hidden + "\n"));
        }

        const string visible = """<div style="float:left;width:100px;height:20px"></div>""";
        Assert.True(MathF.Abs(Width(icons + visible) - baseline - 100f) < 1f);
        const string hiddenBlock = """<div style="display:none">hidden</div>""";
        Assert.Equal(baseline, Width(formatted + hiddenBlock + "\n"));
        Assert.True(
            Width("""<a href="#">CMS</a><div style="display:none;float:left">A much longer hidden string</div>""")
                < baseline,
            "a hidden float must not contribute its own content width");
    }
}

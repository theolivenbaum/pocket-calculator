// upstream 423e58a and merge ab19571: a float keeps its percentage width when it also has a
// percentage margin. Ports layout_test.rs
// `a_float_keeps_its_percentage_width_when_it_has_a_percentage_margin` (over the top of
// render-repros/float-percentage-margin.html) and
// `percentage_float_margins_resolve_against_containing_width_on_every_side`.
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

public sealed class FloatPercentageMarginTests
{
    [Fact]
    public void AFloatKeepsItsPercentageWidthWhenItHasAPercentageMargin()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <!doctype html><style>
              body { margin: 0 }
              .row { width: 1200px }
              .row:before, .row:after { content: " "; display: table }
              .row:after { clear: both }
              #offset { float: left; width: 83.33333333%; margin-left: 8.33333333% }
              #plain  { float: left; width: 83.33333333% }
              #inner  { width: 300px; height: 40px }
              #after  { height: 20px }
            </style>
            <div class="row" id="row-offset"><div id="offset"><div id="inner">offset</div></div></div>
            <div class="row" id="row-plain"><div id="plain"><div>plain</div></div></div>
            <div id="after">after</div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1400f, 1000f));
        Rect R(string id) => layout.Rects[tree.GetElementById(id)!.Value];
        Rect offset = R("offset");
        Assert.True(MathF.Abs(offset.Width - 1000f) < 1f, $"width: {offset.Width}");
        Assert.True(MathF.Abs(offset.X - 100f) < 1f, $"offset: {offset.X}");
        Assert.True(MathF.Abs(offset.Width - R("plain").Width) < 1f);
        Assert.True(R("row-offset").Height > 0f, "the row must contain its float");
    }

    [Theory]
    [InlineData("left", "margin-left:8.33333333%", 100f, 40f)]
    [InlineData("left", "margin-left:-8.33333333%", -100f, 40f)]
    [InlineData("left", "margin-right:8.33333333%", 0f, 40f)]
    [InlineData("left", "margin-top:8.33333333%", 0f, 140f)]
    [InlineData("left", "margin-bottom:8.33333333%", 0f, 140f)]
    [InlineData("right", "margin-left:8.33333333%", 200f, 40f)]
    [InlineData("right", "margin-right:8.33333333%", 100f, 40f)]
    public void PercentageFloatMarginsResolveAgainstContainingWidthOnEverySide(
        string side, string margin, float expectedX, float expectedHeight)
    {
        DomTree tree = HtmlParsing.ParseHtml($$"""
            <style>
              body{margin:0} #row{width:1200px}
              #row::after{content:" ";display:table;clear:both}
              #column{float:{{side}};width:83.33333333%;{{margin}}}
              #inner{width:300px;height:40px} #after{height:20px}
            </style><div id="row"><div id="column"><div id="inner"></div></div></div><div id="after"></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1400f, 1000f));
        Rect R(string id) => layout.Rects[tree.GetElementById(id)!.Value];
        Assert.True(MathF.Abs(R("column").Width - 1000f) < 1f, $"{margin}: {R("column").Width}");
        Assert.True(MathF.Abs(R("column").X - expectedX) < 1f, $"{margin}: {R("column").X}");
        Assert.True(MathF.Abs(R("row").Height - expectedHeight) < 1f, $"{margin}: {R("row").Height}");
        Assert.True(MathF.Abs(R("after").Y - expectedHeight) < 1f, $"{margin}: following flow overlaps");
    }
}

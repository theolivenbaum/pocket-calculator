// upstream 9f64ce9: a float that overflows its band by one f32 rounding step still fits.
// Ports layout_test.rs `nested_offset_float_uses_the_expanded_row_width` and
// `native_float_fit_allows_roundoff_but_not_real_overflow`.
namespace PocketCalculator.Render.Tests;

using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using Xunit;
using Clear = PocketCalculator.Render.Layout.Clear;
using NodeId = PocketCalculator.Dom.NodeId;

public sealed class FloatFitToleranceTests
{
    /// <remarks>Chromium (Playwright, 2026-11-18): left 487.5 wide, right 585 at x 720, 35 lower.</remarks>
    [Fact]
    public void NestedOffsetFloatUsesTheExpandedRowWidth()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <!doctype html><style>
            * { box-sizing:border-box } body { margin:0 }
            .container { width:1170px; margin:0 auto; padding:0 15px }
            .row { margin-left:-15px; margin-right:-15px }
            .row::before, .row::after { content:' '; display:table }
            .row::after { clear:both }
            .full, #left, #right { float:left; padding:0 15px }
            .full { width:100% }
            #left { width:41.66666667%; height:520px }
            #right { width:50%; margin-left:8.33333333%; margin-top:35px; height:470px }
            </style><div class=container><div class=row><div class=full>
                <div class=row><div id=left></div><div id=right></div></div>
            </div></div></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (1440f, 1000f));
        Rect left = layout.Rects[tree.GetElementById("left")!.Value];
        Rect right = layout.Rects[tree.GetElementById("right")!.Value];
        Assert.True(MathF.Abs(left.Width - 487.5f) < 1f, $"{left}");
        Assert.True(MathF.Abs(right.Width - 585f) < 1f, $"{right}");
        Assert.True(MathF.Abs(right.X - 720f) < 1f, $"{right}");
        Assert.True(MathF.Abs(right.Y - left.Y - 35f) < 1f, $"{left} {right}");
    }

    [Fact]
    public void NativeFloatFitAllowsRoundoffButNotRealOverflow()
    {
        // The parsed 41.66666667% column is one rounding step wider than 487.5px.
        float firstWidth = 1170.0f * (41.66666667f / 100.0f);
        foreach ((float width, float inset0, float inset1) in new[]
        {
            (1170.0f, 0.0f, 0.0f),
            (1140.0f, -15.0f, -15.0f),
            (1140.0f, -15.25f, -15.25f),
            (1170.0f, 0.25f, 0.25f),
        })
        {
            foreach (FloatDirection direction in new[] { FloatDirection.Left, FloatDirection.Right })
            {
                foreach (float extra in new[] { 0.0f, 1.0f / 64.0f, 1.0f })
                {
                    var context = new FloatContext();
                    context.SetWidth(width);
                    context.PlaceFloatedBox(
                        new Size<float>(firstWidth, 20.0f), 0.0f, inset0, inset1, direction, Clear.None);
                    float secondWidth = width - inset0 - inset1 - 487.5f + extra;
                    Point<float> second = context.PlaceFloatedBox(
                        new Size<float>(secondWidth, 20.0f), 0.0f, inset0, inset1, direction, Clear.None);
                    Assert.True(
                        second.Y == (extra == 0.0f ? 0.0f : 20.0f),
                        $"width={width}, insets=[{inset0}, {inset1}], direction={direction}, extra={extra}: {second.Y}");
                }
            }
        }

        // At large coordinates, one f32 step is real visible overflow, not a subpixel allowance.
        foreach (FloatDirection direction in new[] { FloatDirection.Left, FloatDirection.Right })
        {
            var context = new FloatContext();
            context.SetWidth(1_048_576.0f);
            context.PlaceFloatedBox(new Size<float>(524_288.125f, 20.0f), 0.0f, 0.0f, 0.0f, direction, Clear.None);
            Point<float> next = context.PlaceFloatedBox(
                new Size<float>(524_288.0f, 20.0f), 0.0f, 0.0f, 0.0f, direction, Clear.None);
            Assert.Equal(20.0f, next.Y);
        }
    }
}

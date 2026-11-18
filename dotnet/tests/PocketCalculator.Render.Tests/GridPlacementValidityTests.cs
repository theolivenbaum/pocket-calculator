// upstream 025088c (layout_test.rs grid_placement_with_zero_span_is_ignored): `span 0` and a
// line number of 0 are invalid grid lines (CSS Grid 1 section 8.3), so the declaration is
// dropped like any other invalid one.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class GridPlacementValidityTests
{
    [Fact]
    public void GridPlacementWithZeroSpanIsIgnored()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <html><head><style>
              html,body{margin:0}
              .grid{--n:0;display:grid;grid-template-columns:repeat(6,1fr);width:600px}
              #literal{grid-column:span 0 / -1}
              #variable{grid-column:span var(--n) / -1}
              #kept{grid-column:2 / 4;grid-column:span 0 / -1}
              #side{grid-column-start:3;grid-column-start:span 0}
              #zero{grid-row:0}
            </style></head><body>
              <div class="grid"><div id="literal">x</div></div>
              <div class="grid"><div id="variable">x</div></div>
              <div class="grid"><div id="kept">x</div></div>
              <div class="grid"><div id="side">x</div></div>
              <div class="grid"><div id="zero">x</div></div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect R(string id) => laid.Rects[tree.GetElementById(id) ?? throw new InvalidOperationException(id)];
        foreach (string id in new[] { "literal", "variable", "zero" })
        {
            Rect r = R(id);
            Assert.True(MathF.Abs(r.X) < 0.01f && MathF.Abs(r.Width - 100f) < 0.01f, $"{id}: {r}");
        }

        Rect kept = R("kept");
        Assert.True(MathF.Abs(kept.X - 100f) < 0.01f && MathF.Abs(kept.Width - 200f) < 0.01f, $"kept: {kept}");
        Rect side = R("side");
        Assert.True(MathF.Abs(side.X - 200f) < 0.01f && MathF.Abs(side.Width - 100f) < 0.01f, $"side: {side}");
    }
}

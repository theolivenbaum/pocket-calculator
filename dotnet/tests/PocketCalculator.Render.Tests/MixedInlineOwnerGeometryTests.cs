// upstream 62e8e7f: an inline wrapper in a mixed block keeps its geometry. Ports layout_test.rs
// `mixed_content_shaped_runs_retain_inline_owner_geometry`.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class MixedInlineOwnerGeometryTests
{
    [Theory]
    [InlineData("ABCD")]
    [InlineData("AB<b id='nested'>CD</b>")]
    public void MixedContentShapedRunsRetainInlineOwnerGeometry(string content)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<style>body{margin:0;font:20px/24px monospace}</style>"
            + "<div id='parent'>AAAA<div style='height:30px'></div>"
            + $"<span id='owner'>{content}</span></div>");
        DomLayout layout = RenderDom.LayoutDom(tree, (200f, 200f));
        NodeId owner = tree.GetElementById("owner")!.Value;
        Assert.True(layout.Rects.TryGetValue(owner, out Rect rect), "shaped inline owner has geometry");
        Assert.True(
            MathF.Abs(rect.X) < 0.01f && rect.Y is >= 52f and <= 57f
            && rect.Width is >= 40f and <= 55f && rect.Height is >= 18f and <= 26f,
            $"inline following a 24px line and 30px block: {rect}");
        Assert.True(layout.InlineFragments.TryGetValue(owner, out List<Rect>? pieces), "canonical inline fragments");
        Assert.NotEmpty(pieces!);
        foreach (Rect piece in pieces!)
        {
            Assert.True(
                piece.Width > 0f && piece.Height > 0f && piece.X >= rect.X && piece.Y >= rect.Y
                && piece.X + piece.Width <= rect.X + rect.Width + 0.01f
                && piece.Y + piece.Height <= rect.Y + rect.Height + 0.01f,
                $"inline fragments remain within their owner bounds: {piece}, {rect}");
        }

        NodeId parent = tree.GetElementById("parent")!.Value;
        Assert.True(MathF.Abs(layout.Rects[parent].Height - 78f) < 0.01f, $"{layout.Rects[parent]}");
        if (tree.GetElementById("nested") is { } nestedId)
        {
            Assert.True(layout.Rects.TryGetValue(nestedId, out Rect nested), "nested inline owner has geometry");
            Assert.True(
                nested.X is >= 20f and <= 28f && nested.Width is >= 20f and <= 28f
                && MathF.Abs(nested.Y - rect.Y) < 1f,
                $"nested text bounds: {nested}");
        }
    }
}

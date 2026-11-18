// upstream df5ee65: `transform` does not apply to a non-replaced inline box. Ports paint.rs
// `non_replaced_inline_ignores_transform_for_geometry`.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class InlineTransformTests
{
    /// <remarks>Chromium (Playwright, 2026-11-18): the inline span stays at x 0, the inline-block moves.</remarks>
    [Fact]
    public void NonReplacedInlineIgnoresTransformForGeometry()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <html><body style="margin:0">
              <span id="inline" style="transform:translateX(50px)">abc</span>
              <span id="atomic" style="display:inline-block;transform:translateX(50px)">abc</span>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (300f, 100f), null, resources)!;
        NodeId inline = tree.GetElementById("inline")!.Value;
        NodeId atomic = tree.GetElementById("atomic")!.Value;

        Assert.False(prepared.Layout.Transforms.ContainsKey(inline));
        Assert.True(prepared.Layout.Transforms.ContainsKey(atomic));
        Assert.True(prepared.DocumentRect(atomic)!.Value.X >= 50f);
        Assert.True(prepared.DocumentRect(inline)!.Value.X < 1f, $"{prepared.DocumentRect(inline)}");
    }
}

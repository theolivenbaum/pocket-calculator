// upstream 0e99b3a: a percentage height resolves against a definite block containing block
// across the anonymous inline-run wrapper. Ports layout_test.rs
// `inline_block_percentage_height_uses_definite_block_content_height`.
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

public sealed class InlinePercentageHeightTests
{
    [Theory]
    [InlineData("percentage", 32f, 16f)]
    [InlineData("pixels", 32f, 16f)]
    [InlineData("content-box", 22f, 20f)]
    [InlineData("half", 22f, 10f)]
    [InlineData("indefinite", 32f, 2f)]
    public void InlineBlockPercentageHeightUsesDefiniteBlockContentHeight(string name, float width, float height)
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <!doctype html>
            <style>
              html, body { margin:0 }
              .switch { position:relative; width:32px; height:16px; line-height:1 }
              input { position:absolute; width:100%; height:100%; margin:0; opacity:0; z-index:-1000 }
              label { display:inline-block; box-sizing:border-box; width:100%; height:100%; border:1px solid }
              .padded { height:30px; box-sizing:border-box; padding:4px; border:1px solid }
              .indefinite { height:auto; min-height:40px }
            </style>
            <div class="switch"><input type="checkbox"><label id="percentage"></label></div>
            <div class="switch"><input type="checkbox"><label id="pixels" style="height:16px"></label></div>
            <div class="switch padded"><label id="content-box"></label></div>
            <div class="switch padded"><label id="half" style="height:50%"></label></div>
            <div class="switch indefinite"><label id="indefinite"></label></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect rect = layout.Rects[tree.GetElementById(name)!.Value];
        Assert.Equal((width, height), (rect.Width, rect.Height));
    }
}

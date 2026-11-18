// upstream 6234ed3: a stretched grid control still contributes its intrinsic size. Ports
// layout_test.rs `nested_grid_input_contributes_intrinsic_height_without_preventing_stretch`.
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

public sealed class StretchedGridControlTests
{
    [Theory]
    [InlineData("auto-input", "auto-editor", 22f)]
    [InlineData("short-input", "short-editor", 12f)]
    public void NestedGridInputContributesIntrinsicHeightWithoutPreventingStretch(
        string input, string editor, float height)
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <style>
              html, body { margin:0; font:14px Arial; line-height:22px }
              .outer { display:grid; width:214px; align-items:center }
              .editor { display:inline-grid; grid-area:1 / 1 / 2 / 3;
                        grid-template-columns:0 min-content }
              input { display:block; width:100%; grid-area:1 / 2;
                      font:inherit; min-width:2px; padding:0; border:0 }
              .short { grid-template-rows:12px }
            </style>
            <div class="outer"><div class="editor" id="auto-editor"><input id="auto-input"></div></div>
            <div class="outer"><div class="editor short" id="short-editor"><input id="short-input"></div></div>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect inputRect = layout.Rects[tree.GetElementById(input)!.Value];
        Rect editorRect = layout.Rects[tree.GetElementById(editor)!.Value];
        Assert.True(inputRect.Height == height, $"{input}: {inputRect}");
        Assert.True(editorRect.Height == height, $"{editor}: {editorRect}");
        Assert.True(inputRect.Width >= 2f, $"{input}: {inputRect}");
    }
}

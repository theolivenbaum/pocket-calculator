// upstream 2c81268: a closed HTML <dialog> is hidden by a UA `display: none` rule below author
// styles. Ports layout_test.rs `closed_html_dialogs_use_a_ua_display_rule_below_author_styles`.
// The port already had the rule (Dom/DomCascade.cs).
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public sealed class ClosedDialogLayoutTests
{
    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void ClosedHtmlDialogsUseAUaDisplayRuleBelowAuthorStyles()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <!doctype html><style>
                html,body{margin:0} dialog{width:80px;height:30px}
                #authored{display:block} #contents{display:contents}
                #contents > div{width:20px;height:10px}
            </style><dialog id=closed><div id=closed_child>hidden</div></dialog>
            <dialog id=opened open></dialog><dialog id=authored></dialog>
            <dialog id=contents><div id=visible_child></div></dialog>
            <svg><dialog id=svg_dialog></dialog></svg>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (400f, 300f));
        foreach (string name in (string[])["closed", "closed_child"])
        {
            Assert.False(layout.Rects.ContainsKey(Id(tree, name)), $"{name} generated a box");
        }

        foreach (string name in (string[])["opened", "authored"])
        {
            Rect rect = layout.Rects[Id(tree, name)];
            Assert.Equal((80f, 30f), (rect.Width, rect.Height));
        }

        Assert.True(layout.Rects.ContainsKey(Id(tree, "visible_child")));
        Assert.NotEqual(Display.None, layout.Styles[Id(tree, "svg_dialog")].Display);
    }
}

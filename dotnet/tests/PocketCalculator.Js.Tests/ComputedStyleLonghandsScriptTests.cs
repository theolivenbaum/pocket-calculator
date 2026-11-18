using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream c280615: five longhands and the <c>cssFloat</c> alias through
/// <c>getComputedStyle()</c>. Expected strings are Chromium 147's for the same markup.
/// </summary>
public sealed class ComputedStyleLonghandsScriptTests
{
    private static string[] Evaluate(string html, string script)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(800.0, 600.0);
        rt.RunPageInit();
        var result = rt.Evaluate(script) ?? throw new InvalidOperationException("no result");
        return [.. result.AsArray().Select(static node => node!.GetValue<string>())];
    }

    [Fact]
    public void LonghandsAndCssFloatReadFromTheSnapshot()
    {
        string[] result = Evaluate(
            """
            <html><head><style>
              #t { vertical-align: middle; list-style-type: square; border-collapse: collapse;
                   table-layout: fixed; text-transform: uppercase; float: left; word-spacing: 2px }
            </style></head><body><table id="t"><tr><td>x</td></tr></table><div id="d">a</div></body></html>
            """,
            """
            (() => {
              const t = getComputedStyle(document.getElementById("t"));
              const d = getComputedStyle(document.getElementById("d"));
              return [t.verticalAlign, t.listStyleType, t.borderCollapse, t.tableLayout, t.textTransform,
                t.cssFloat, t.getPropertyValue("float"), t.wordSpacing,
                d.verticalAlign, d.listStyleType, d.borderCollapse, d.tableLayout, d.textTransform,
                d.cssFloat, d.wordSpacing];
            })()
            """);
        Assert.Equal(
            ["middle", "square", "collapse", "fixed", "uppercase", "left", "left", "2px",
             "baseline", "disc", "separate", "auto", "none", "none", "0px"],
            result);
    }

    [Fact]
    public void InlineStyleCssFloatIsTheFloatProperty()
    {
        string[] result = Evaluate(
            "<html><body><div id=\"d\"></div></body></html>",
            """
            (() => {
              const d = document.getElementById("d");
              d.style.cssFloat = "right";
              return [d.style.cssFloat, d.style.getPropertyValue("float"), d.getAttribute("style"),
                getComputedStyle(d).cssFloat];
            })()
            """);
        Assert.Equal(["right", "right", "float: right;", "right"], result);
    }
}

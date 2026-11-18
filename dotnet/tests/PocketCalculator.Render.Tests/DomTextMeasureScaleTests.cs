using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// <c>measure_text</c> / <c>text_width</c> scale by the em square (upstream 9a3d12e).
/// </summary>
/// <remarks>
/// The reference used to scale by the font's hhea height (ab_glyph's <c>PxScale</c>), which
/// made every advance ~11% short of Chromium's for Liberation Sans (2048/2288), and the port
/// reproduced that as a deviation. Upstream now measures em-based, matching Chromium and the
/// shaped inline path, and so does the port.
/// </remarks>
public sealed class DomTextMeasureScaleTests
{
    /// <summary>Port of <c>measure_text_matches_browser_advance_widths</c>: Chromium 147,
    /// Liberation Sans (its Arial substitute) at 14px.</summary>
    [Theory]
    [InlineData("abcdefghij", 63.8f)]
    [InlineData("ab", 15.6f)]
    [InlineData("Create new form", 103.0f)]
    public void MeasureTextMatchesBrowserAdvanceWidths(string text, float chromium)
    {
        float width = DomTextMeasure.MeasureText(text, 14f, isBold: false, "Arial");
        Assert.True(MathF.Abs(width - chromium) < 1f, $"MeasureText({text}) = {width}, Chromium gives {chromium}");
    }

    /// <summary>
    /// Bold is the reference's synthetic advance of one pixel per glyph, on top of the scaled
    /// width, and the string has 21 non-control characters.
    /// </summary>
    [Fact]
    public void BoldAddsOnePixelPerGlyphOnTopOfTheScaledWidth()
    {
        const string label = "Launch simulated demo";
        float regular = DomTextMeasure.MeasureText(label, 16f, isBold: false, "sans-serif");
        float bold = DomTextMeasure.MeasureText(label, 16f, isBold: true, "sans-serif");
        Assert.Equal(21f, bold - regular, 0.01f);
    }

    /// <summary>The scale is linear in the CSS size for every bundled fallback face.</summary>
    [Theory]
    [InlineData("sans-serif")]
    [InlineData("serif")]
    [InlineData("monospace")]
    [InlineData("system-ui")]
    public void ScaleIsLinearInTheCssSize(string family)
    {
        float at16 = DomTextMeasure.MeasureText("Launch simulated demo", 16f, isBold: false, family);
        float at32 = DomTextMeasure.MeasureText("Launch simulated demo", 32f, isBold: false, family);
        Assert.Equal(at16 * 2f, at32, 0.05f);
    }

    /// <summary>An empty or degenerate request measures zero rather than throwing.</summary>
    [Theory]
    [InlineData("", 16f)]
    [InlineData("x", 0f)]
    [InlineData("x", -1f)]
    public void DegenerateRequestsMeasureZero(string text, float size)
    {
        Assert.Equal(0f, DomTextMeasure.MeasureText(text, size, isBold: false, "sans-serif"));
    }
}

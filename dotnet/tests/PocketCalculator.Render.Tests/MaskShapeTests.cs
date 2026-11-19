using PocketCalculator.Render.Css;
using SkiaSharp;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Port addition: <see cref="Mask.Shape"/>, which lets <see cref="Surface.Draw"/> treat a mask of
/// full coverage over a pixel-aligned rectangle as a clip, and an empty one as nothing to paint,
/// instead of blending the whole mask over a full-surface layer for every primitive.
/// </summary>
public class MaskShapeTests
{
    private static SKPath RectPath(float x, float y, float width, float height)
    {
        using SKPathBuilder builder = new();
        builder.AddRect(SKRect.Create(x, y, width, height));
        return builder.Detach();
    }

    [Fact]
    public void AFreshMaskIsEmpty()
    {
        Assert.Equal(MaskShapeKind.Empty, new Mask(40, 30).Shape.Kind);
    }

    [Fact]
    public void AnAlignedRectangleIsARectangleWithItsPixelBounds()
    {
        var mask = new Mask(40, 30);
        using SKPath rect = RectPath(5, 4, 20, 10);
        mask.FillPath(rect, evenOdd: false, antiAlias: true);

        MaskShape shape = mask.Shape;
        Assert.Equal(MaskShapeKind.Rectangle, shape.Kind);
        Assert.Equal(new SKRectI(5, 4, 25, 14), shape.Bounds);
    }

    [Fact]
    public void PartialCoverageOrAShapeIsIrregular()
    {
        var fractional = new Mask(40, 30);
        using SKPath half = RectPath(5.5f, 4, 20, 10);
        fractional.FillPath(half, evenOdd: false, antiAlias: true);
        Assert.Equal(MaskShapeKind.Irregular, fractional.Shape.Kind);

        var round = new Mask(40, 30);
        using SKPathBuilder builder = new();
        builder.AddCircle(20, 15, 10);
        using SKPath circle = builder.Detach();
        round.FillPath(circle, evenOdd: false, antiAlias: true);
        Assert.Equal(MaskShapeKind.Irregular, round.Shape.Kind);
    }

    [Fact]
    public void ChangingTheMaskClassifiesItAgain()
    {
        var mask = new Mask(40, 30);
        Assert.Equal(MaskShapeKind.Empty, mask.Shape.Kind);

        using SKPath rect = RectPath(0, 0, 40, 30);
        mask.FillPath(rect, evenOdd: false, antiAlias: false);
        Assert.Equal(MaskShapeKind.Rectangle, mask.Shape.Kind);

        mask.Invert();
        Assert.Equal(MaskShapeKind.Empty, mask.Shape.Kind);

        mask.Invert();
        using SKPath inner = RectPath(10, 10, 10, 10);
        mask.IntersectPath(inner, evenOdd: false, antiAlias: false);
        Assert.Equal(new SKRectI(10, 10, 20, 20), mask.Shape.Bounds);
        Assert.Equal(mask.Shape, mask.Clone().Shape);
    }

    [Fact]
    public void ARectangleMaskClipsAndAnEmptyOnePaintsNothing()
    {
        using var pixmap = new Pixmap(40, 30);
        var clip = new Mask(40, 30);
        using SKPath clipRect = RectPath(10, 10, 10, 10);
        clip.FillPath(clipRect, evenOdd: false, antiAlias: false);
        using SKPath everything = RectPath(0, 0, 40, 30);

        Surface.FillPath(pixmap, everything, new RgbaColor(255, 0, 0, 255), false, Affine2.Identity, clip);
        Assert.Equal(255, pixmap.Pixel(15, 15)!.Value.A);
        Assert.Equal(0, pixmap.Pixel(9, 15)!.Value.A);
        Assert.Equal(0, pixmap.Pixel(20, 15)!.Value.A);

        using var untouched = new Pixmap(40, 30);
        Surface.FillPath(untouched, everything, new RgbaColor(255, 0, 0, 255), false, Affine2.Identity, new Mask(40, 30));
        Assert.All(untouched.Pixels, pixel => Assert.Equal(0, pixel.A));
    }
}

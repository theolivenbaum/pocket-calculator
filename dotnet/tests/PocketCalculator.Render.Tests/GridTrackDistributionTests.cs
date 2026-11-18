// Ports of the vendored taffy unit tests in vendor/taffy/src/compute/grid/track_sizing.rs
// added by upstream 281470c: redistribution must stop when f32 rounding stalls it.
using PocketCalculator.Render.Layout;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class GridTrackDistributionTests
{
    [Fact]
    public void TrackDistributionStopsWhenRoundingPreventsAllProgress()
    {
        List<GridTrack> list =
        [
            GridTrack.Gutter(LengthPercentage.Zero),
            GridTrack.Gutter(LengthPercentage.Zero),
            GridTrack.Gutter(LengthPercentage.Zero),
        ];
        (float Base, float Limit, float Proportion, float Incurred)[] values =
        [
            (0.71128696f, 780789.875f, 0.6958633f, 780789.1875f),
            (99.2123f, 429551.4375f, 1.2281497f, 429452.21875f),
            (8.539768f, 436054.53125f, 0.47339195f, 260653.515625f),
        ];
        for (int i = 0; i < list.Count; i++)
        {
            list[i].BaseSize = values[i].Base;
            list[i].GrowthLimit = values[i].Limit;
            list[i].ContentAlignmentAdjustment = values[i].Proportion;
            list[i].ItemIncurredIncrease = values[i].Incurred;
        }

        float[] before = [.. list.Select(static t => t.ItemIncurredIncrease)];
        int calls = 0;
        float remaining = TrackSizing.DistributeSpaceUpToLimits(
            64365076.0f,
            new TrackSlice(list),
            static _ => true,
            static t => t.ContentAlignmentAdjustment,
            static t => t.BaseSize,
            t =>
            {
                calls++;
                Assert.True(calls < 1000, "track sizing repeated a state without progress");
                return t.GrowthLimit;
            });
        Assert.Equal(64365076.0f, remaining);
        Assert.Equal(before, list.Select(static t => t.ItemIncurredIncrease));
    }

    [Fact]
    public void TrackDistributionContinuesWhenOnlyATrackProgresses()
    {
        List<GridTrack> list = [GridTrack.Gutter(LengthPercentage.Zero), GridTrack.Gutter(LengthPercentage.Zero)];
        list[0].GrowthLimit = 1e-9f;
        list[1].GrowthLimit = 100.0f;
        float remaining = TrackSizing.DistributeSpaceUpToLimits(
            100.0f, new TrackSlice(list), static _ => true, static _ => 1.0f,
            static t => t.BaseSize, static t => t.GrowthLimit);
        Assert.Equal(0.0f, remaining);
        Assert.Equal(1e-9f, list[0].ItemIncurredIncrease);
        Assert.Equal(100.0f, list[1].ItemIncurredIncrease);
    }

    [Fact]
    public void TrackDistributionPreservesWeightedLimitsAndRemainingSpace()
    {
        List<GridTrack> list = [GridTrack.Gutter(LengthPercentage.Zero), GridTrack.Gutter(LengthPercentage.Zero)];
        list[0].GrowthLimit = 10.0f;
        list[1].GrowthLimit = 30.0f;
        list[0].ContentAlignmentAdjustment = 1.0f;
        list[1].ContentAlignmentAdjustment = 3.0f;
        float remaining = TrackSizing.DistributeSpaceUpToLimits(
            50.0f, new TrackSlice(list), static _ => true, static t => t.ContentAlignmentAdjustment,
            static t => t.BaseSize, static t => t.GrowthLimit);
        Assert.Equal(10.0f, remaining);
        Assert.Equal(10.0f, list[0].ItemIncurredIncrease);
        Assert.Equal(30.0f, list[1].ItemIncurredIncrease);
    }
}

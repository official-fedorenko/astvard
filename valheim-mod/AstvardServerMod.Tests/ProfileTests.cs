using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The road's smoothing: flat across its width, and along its length following the land
/// with the lumps taken off. What a player sees is whether the road meets the ground at
/// both ends, whether a hillside road is left alone, and whether a bump goes away.
/// </summary>
public class ProfileTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void FlatGroundStaysFlat()
    {
        var smoothed = Geometry.SmoothProfile(Enumerable.Repeat(31.5f, 40).ToList(), 5);
        Assert.All(smoothed, h => Assert.Equal(31.5f, h, 4));
    }

    [Fact]
    public void AnEvenSlopeIsLeftExactlyAsItWas()
    {
        // A road up a hillside should climb it, not be cut into steps or bowed. Every
        // window is symmetric, so a straight line averages to itself.
        var slope = Enumerable.Range(0, 60).Select(i => 30f + i * 0.35f).ToList();
        var smoothed = Geometry.SmoothProfile(slope, 6);

        for (var i = 0; i < slope.Count; i++)
            Assert.True(Math.Abs(slope[i] - smoothed[i]) < Tolerance,
                $"point {i} moved from {slope[i]} to {smoothed[i]}");
    }

    [Fact]
    public void TheRoadMeetsTheGroundAtBothEnds()
    {
        // Otherwise it would start and stop on a little step up or down.
        var rough = new List<float> { 30f, 33f, 29f, 34f, 31f, 35f, 30f, 32f, 36f };
        var smoothed = Geometry.SmoothProfile(rough, 4);

        Assert.Equal(rough[0], smoothed[0], 4);
        Assert.Equal(rough[^1], smoothed[^1], 4);
    }

    [Fact]
    public void ABumpIsTakenOffAndSpreadRatherThanMoved()
    {
        var ground = Enumerable.Repeat(30f, 41).ToList();
        ground[20] = 32f;
        var smoothed = Geometry.SmoothProfile(ground, 5);

        // Most of a two metre lump goes.
        Assert.True(smoothed[20] - 30f < 0.2f, $"the bump still stands {smoothed[20] - 30f:F2} m");
        // And none of the ground around it is pushed below where it was.
        Assert.All(smoothed, h => Assert.True(h >= 30f - Tolerance));
        // Far enough away, nothing changes at all.
        Assert.Equal(30f, smoothed[2], 4);
        Assert.Equal(30f, smoothed[38], 4);
    }

    [Fact]
    public void AShortRunIsReturnedUntouched()
    {
        Assert.Equal(new[] { 30f, 40f }, Geometry.SmoothProfile(new List<float> { 30f, 40f }, 5));
        Assert.Empty(Geometry.SmoothProfile(new List<float>(), 5));
    }

    [Fact]
    public void TheNearestPlaceIsAFractionalIndexWithItsDistance()
    {
        var path = new List<Vec2> { new(0f, 0f), new(1f, 0f), new(2f, 0f), new(3f, 0f) };

        var along = Geometry.NearestOnPath(path, 1.5f, 2f, out var distance);
        Assert.Equal(1.5f, along, 4);
        Assert.Equal(2f, distance, 4);

        // Past the end it clamps to the last point.
        along = Geometry.NearestOnPath(path, 5f, 0f, out distance);
        Assert.Equal(3f, along, 4);
        Assert.Equal(2f, distance, 4);
    }

    [Fact]
    public void AProfileReadsBetweenItsPointsInAStraightLine()
    {
        var profile = new List<float> { 30f, 32f, 31f };

        Assert.Equal(31f, Geometry.ProfileAt(profile, 0.5f), 4);
        Assert.Equal(31.5f, Geometry.ProfileAt(profile, 1.5f), 4);
        Assert.Equal(30f, Geometry.ProfileAt(profile, -3f), 4);
        Assert.Equal(31f, Geometry.ProfileAt(profile, 9f), 4);
    }
}

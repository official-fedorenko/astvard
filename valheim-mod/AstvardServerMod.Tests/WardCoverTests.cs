using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The game answers the ward question for a disc - a point and a radius - while the tools
/// rework a stroke or a square. These pin that the discs handed to it cover everything the
/// tool will touch: a ward the discs miss is a ward the tool walks straight into.
/// </summary>
public class WardCoverTests
{
    private const float Tolerance = 1e-3f;

    private static float NearestSample(IList<Vec2> points, float x, float z)
    {
        var best = float.MaxValue;
        foreach (var p in points)
            best = Math.Min(best, (float)Math.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z)));
        return best;
    }

    /// <summary>
    /// The case the half step exists for. Two samples two metres apart and a road a metre
    /// to either side: a point just inside the edge, level with the middle of the step,
    /// is on the road and yet 1.41 m from both samples.
    /// </summary>
    [Fact]
    public void BesideTheMiddleOfAStepIsWhatTheHalfStepIsFor()
    {
        var path = new List<Vec2> { new Vec2(0f, 0f), new Vec2(2f, 0f) };
        const float reach = 1f;

        Assert.True(Geometry.DistanceToPath(path, 0, 1, 1f, 0.99f) <= reach);
        Assert.True(NearestSample(path, 1f, 0.99f) > reach, "a disc of the bare reach would miss it");
        Assert.True(NearestSample(path, 1f, 0.99f) <= Geometry.DiscCover(path, reach));
    }

    [Theory]
    [InlineData(1f, 3.5f)]
    [InlineData(1f, 0.75f)]
    [InlineData(4f, 1f)]
    public void DiscsAlongARoadCoverEverythingWithinReachOfIt(float step, float reach)
    {
        // A strong bend, so the samples are uneven, and a coarse step as well as the
        // metre the road tool uses: the rule has to hold for the spacing it is given.
        var path = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(60f, 40f), 15f, step);
        var last = path.Count - 1;
        var cover = Geometry.DiscCover(path, reach);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in path)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minZ = Math.Min(minZ, p.Z);
            maxZ = Math.Max(maxZ, p.Z);
        }

        var onRoad = 0;
        for (var x = minX - reach; x <= maxX + reach; x += 0.3f)
        for (var z = minZ - reach; z <= maxZ + reach; z += 0.3f)
        {
            if (Geometry.DistanceToPath(path, 0, last, x, z) > reach) continue;

            onRoad++;
            Assert.True(NearestSample(path, x, z) <= cover + Tolerance,
                $"({x:F1}, {z:F1}) is on the road but outside every disc");
        }

        Assert.True(onRoad > 500, "the sweep should have crossed the whole road");
    }

    [Fact]
    public void ASinglePointIsJudgedByItsReachAlone()
    {
        // The pad laid round the player is a road of one point.
        Assert.Equal(8f, Geometry.DiscCover(new List<Vec2> { new Vec2(5f, -3f) }, 8f));
    }

    [Theory]
    [InlineData(14f, 4f)]
    [InlineData(89f, 4f)]
    [InlineData(10f, 3f)]
    [InlineData(7.3f, 2.2f)]
    [InlineData(0.5f, 4f)]
    public void DiscsOverASquareReachEveryCornerOfIt(float half, float cell)
    {
        var centre = new Vec2(10f, -7f);
        var discs = Geometry.SquareCover(centre, half, cell, out var radius);

        var cells = (int)Math.Ceiling(2.0 * half / cell);
        Assert.Equal(cells * cells, discs.Count);

        // A grid over the square that lands on its edges and corners as well.
        const int samples = 60;
        for (var i = 0; i <= samples; i++)
        for (var j = 0; j <= samples; j++)
        {
            var x = centre.X - half + 2f * half * j / samples;
            var z = centre.Z - half + 2f * half * i / samples;
            Assert.True(NearestSample(discs, x, z) <= radius + Tolerance,
                $"({x:F2}, {z:F2}) is in the square but outside every disc");
        }
    }

    [Theory]
    [InlineData(14f, 4f)]
    [InlineData(89f, 4f)]
    [InlineData(3f, 4f)]
    public void NoDiscIsWiderThanHalfACellsDiagonal(float half, float cell)
    {
        Geometry.SquareCover(new Vec2(0f, 0f), half, cell, out var radius);
        Assert.True(radius <= cell * Math.Sqrt(0.5) + Tolerance);
    }

    /// <summary>
    /// The point of cutting the square up. One disc round it would refuse a ward standing
    /// a cell's width off the middle of a side; a disc per cell does not reach that far.
    /// </summary>
    [Fact]
    public void TheCoverDoesNotReachACellPastTheSquare()
    {
        const float half = 14f;
        const float cell = 4f;
        var discs = Geometry.SquareCover(new Vec2(0f, 0f), half, cell, out var radius);

        Assert.True(NearestSample(discs, half + cell, 0f) > radius);
        Assert.True(NearestSample(discs, 0f, -(half + cell)) > radius);

        // Whereas one disc round the whole square - a cell as big as the square - takes
        // the same place in.
        var whole = Geometry.SquareCover(new Vec2(0f, 0f), half, 2f * half, out var wide);
        Assert.Single(whole);
        Assert.True(NearestSample(whole, half + cell, 0f) <= wide);
    }

    [Fact]
    public void ASquareOfNoSizeIsOnePoint()
    {
        var discs = Geometry.SquareCover(new Vec2(3f, 4f), 0f, 4f, out var radius);

        Assert.Single(discs);
        Assert.Equal(3f, discs[0].X);
        Assert.Equal(4f, discs[0].Z);
        Assert.Equal(0f, radius);
    }
}

using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The world is carved into fixed grids the mod does not get to choose: 64 m zones,
/// and a paint mask with a vertex every few metres. Two shipped bugs came from
/// getting the boundaries of those grids wrong.
/// </summary>
public class GridTests
{
    // Valheim's terrain: 64 m zones, and heightmaps whose vertex spacing varies.
    public static TheoryData<float, int, float> Grids => new()
    {
        { 1f, 32, 0f }, { 1f, 64, 0f }, { 2f, 32, 0f }, { 0.5f, 64, 0f },
        { 1f, 64, 64f }, { 2f, 32, -128f }, { 1f, 32, 512f }, { 2f, 64, -1024f },
    };

    [Theory]
    [MemberData(nameof(Grids))]
    public void VertexAndWorldAreExactInverses(float scale, int width, float origin)
    {
        var size = width + 1;
        var half = size / 2;

        for (var vertex = 0; vertex < size; vertex++)
        {
            var world = Geometry.WorldAt(vertex, origin, scale, half);
            Assert.Equal(vertex, Geometry.VertexAt(world, origin, scale, half));
        }
    }

    [Theory]
    [MemberData(nameof(Grids))]
    public void EachVertexOwnsTheBandAroundItself(float scale, int width, float origin)
    {
        var half = (width + 1) / 2;

        foreach (var vertex in new[] { 0, 1, width / 2, width })
        {
            var centre = Geometry.WorldAt(vertex, origin, scale, half);
            Assert.Equal(vertex, Geometry.VertexAt(centre - scale / 2f + 0.001f, origin, scale, half));
            Assert.Equal(vertex, Geometry.VertexAt(centre + scale / 2f - 0.001f, origin, scale, half));
        }
    }

    [Fact]
    public void ZoneCellSpansHalfEitherSideOfItsCentre()
    {
        // Cell i covers [64i - 32, 64i + 32): every boundary belongs to the cell above
        // it, on both sides of the origin.
        Assert.Equal(0, Geometry.ZoneOf(-32f, 64f));
        Assert.Equal(0, Geometry.ZoneOf(0f, 64f));
        Assert.Equal(0, Geometry.ZoneOf(31.99f, 64f));
        Assert.Equal(1, Geometry.ZoneOf(32f, 64f));
        Assert.Equal(-1, Geometry.ZoneOf(-32.01f, 64f));
        Assert.Equal(-1, Geometry.ZoneOf(-64f, 64f));
    }

    /// <summary>
    /// The reason zone size is asked for in metres rather than in whole cells: a
    /// radius that fits inside one cell should take one, and the same radius at a
    /// corner has to take four, because the grid does not move to suit the player.
    /// </summary>
    [Fact]
    public void AHalfCellRadiusTakesOneCellAtTheCentreAndFourAtACorner()
    {
        Geometry.ZoneRange(0f, 0f, 32f, 64f, out var minX, out var minZ, out var maxX, out var maxZ);
        Assert.Equal(1, (maxX - minX + 1) * (maxZ - minZ + 1));

        Geometry.ZoneRange(32f, 32f, 32f, 64f, out minX, out minZ, out maxX, out maxZ);
        Assert.Equal(4, (maxX - minX + 1) * (maxZ - minZ + 1));
    }

    [Fact]
    public void ZoneRangeAlwaysCoversTheRequestedRadius()
    {
        // Whatever the grid does underneath, every metre asked for must be inside.
        foreach (var centre in new[] { 0f, 17.5f, -63f, 401.25f })
        foreach (var radius in new[] { 32f, 64f, 96f, 128f })
        {
            Geometry.ZoneRange(centre, centre, radius, 64f, out var minX, out var minZ,
                               out var maxX, out var maxZ);

            Assert.True(Geometry.ZoneOf(centre - radius, 64f) >= minX);
            Assert.True(Geometry.ZoneOf(centre + radius - 0.02f, 64f) <= maxX);
            Assert.Equal(minX, minZ);
            Assert.Equal(maxX, maxZ);
        }
    }

    /// <summary>
    /// The brush is solid across nearly the whole radius. A gentler curve would leave
    /// a painted road looking washed out at the edges instead of edged.
    /// </summary>
    [Fact]
    public void FalloffIsFlatAcrossMostOfTheBrush()
    {
        Assert.Equal(1f, Geometry.Falloff(0f, 2f), 3);
        Assert.Equal(0f, Geometry.Falloff(2f, 2f), 3);
        Assert.True(Geometry.Falloff(1.8f, 2f) > 0.7f, "the last tenth should still be mostly solid");
        Assert.Equal(0f, Geometry.Falloff(3f, 2f));
    }
}

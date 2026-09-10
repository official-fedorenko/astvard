using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Filling a closed space with floor. What a player sees: a closed room fills to the
/// walls and no further, a gap is found and shown instead of flooding the world, a
/// floor already there is left alone, and a room built on the plate's grid comes out
/// exactly covered.
/// </summary>
public class FloorFillTests
{
    // Half-metre cells. The start plate covers cells -2 .. 1 both ways.
    private static readonly GridCell[] Plate =
    {
        new(-2, -2), new(-1, -2), new(0, -2), new(1, -2),
        new(-2, -1), new(-1, -1), new(0, -1), new(1, -1),
        new(-2, 0), new(-1, 0), new(0, 0), new(1, 0),
        new(-2, 1), new(-1, 1), new(0, 1), new(1, 1),
    };

    /// <summary>A rectangle of wall one cell thick round cells i0..i1, j0..j1 inside.</summary>
    private static Func<int, int, FloorCell> Room(int i0, int i1, int j0, int j1,
                                                  Func<int, int, bool>? gap = null,
                                                  Func<int, int, bool>? floored = null)
    {
        return (i, j) =>
        {
            var inside = i >= i0 && i <= i1 && j >= j0 && j <= j1;
            var ring = i >= i0 - 1 && i <= i1 + 1 && j >= j0 - 1 && j <= j1 + 1 && !inside;
            if (ring && (gap == null || !gap(i, j))) return FloorCell.Wall;
            if (floored != null && floored(i, j)) return FloorCell.Floored;
            return FloorCell.Free;
        };
    }

    [Fact]
    public void ASixMetreRoomOnThePlatesGridTakesNineWholePlates()
    {
        // Inside from -3 m to 3 m both ways: cells -6 .. 5.
        var region = Geometry.FloodFloor(Plate, Room(-6, 5, -6, 5), null, 128, 12000);
        Assert.True(region.Closed);
        Assert.Equal(144, region.Free.Count);

        var big = new List<GridCell>();
        var small = new List<GridCell>();
        Geometry.FloorTiles(region.Free, big, small);

        Assert.Equal(9, big.Count);
        Assert.Empty(small);
        Assert.Contains(new GridCell(0, 0), big);
        Assert.Contains(new GridCell(-1, 1), big);
    }

    [Fact]
    public void AnOddMetreIsMadeUpWithSmallPlates()
    {
        // Five metres by four: -3 .. 2 m across (cells -6 .. 3), -1 .. 3 m along (cells -2 .. 5).
        var region = Geometry.FloodFloor(Plate, Room(-6, 3, -2, 5), null, 128, 12000);
        Assert.True(region.Closed);

        var big = new List<GridCell>();
        var small = new List<GridCell>();
        Geometry.FloorTiles(region.Free, big, small);

        // Twenty square metres, every one of them covered once.
        Assert.Equal(20, big.Count * 4 + small.Count);
        Assert.NotEmpty(small);
    }

    [Fact]
    public void AGapLetsTheFloodOutAndTheWayOutRunsThroughIt()
    {
        // A metre-wide gap in the east wall, cells 6 at j = 0 and 1.
        var region = Geometry.FloodFloor(Plate,
            Room(-6, 5, -6, 5, gap: (i, j) => i == 6 && (j == 0 || j == 1)), null, 40, 12000);

        Assert.False(region.Closed);
        Assert.False(region.TooBig);
        Assert.Contains(region.WayOut, cell => cell.I == 6 && (cell.J == 0 || cell.J == 1));
        // It starts on the plate.
        Assert.Contains(region.WayOut[0], Plate);
    }

    [Fact]
    public void AFloorAlreadyThereIsCrossedButNotLaidOn()
    {
        // The south-west quarter is floored already.
        var region = Geometry.FloodFloor(Plate,
            Room(-6, 5, -6, 5, floored: (i, j) => i < -2 && j < -2), null, 128, 12000);

        Assert.True(region.Closed);
        Assert.Equal(16, region.Floored.Count);

        var big = new List<GridCell>();
        var small = new List<GridCell>();
        Geometry.FloorTiles(region.Free, big, small);
        Assert.Equal(8, big.Count);
        Assert.DoesNotContain(new GridCell(-1, -1), big);
    }

    [Fact]
    public void AnLShapedRoomFillsBothArms()
    {
        Func<int, int, FloorCell> classify = (i, j) =>
        {
            // Inside: a 4 m square round the plate, plus an arm 4 m long going east.
            var body = i >= -4 && i <= 3 && j >= -4 && j <= 3;
            var arm = i >= 4 && i <= 11 && j >= -4 && j <= -1;
            if (body || arm) return FloorCell.Free;
            return FloorCell.Wall;
        };

        var region = Geometry.FloodFloor(Plate, classify, null, 128, 12000);
        Assert.True(region.Closed);
        Assert.Equal(64 + 32, region.Free.Count);

        var big = new List<GridCell>();
        var small = new List<GridCell>();
        Geometry.FloorTiles(region.Free, big, small);
        Assert.Equal(24, big.Count * 4 + small.Count);
    }

    [Fact]
    public void ASpaceTooBigToFillIsTurnedDown()
    {
        var region = Geometry.FloodFloor(Plate, Room(-100, 100, -100, 100), null, 400, 1000);

        Assert.False(region.Closed);
        Assert.True(region.TooBig);
    }

    [Fact]
    public void APlateStartedInsideAWallFindsNothing()
    {
        var region = Geometry.FloodFloor(Plate, (i, j) => FloorCell.Wall, null, 128, 12000);

        Assert.False(region.Closed);
        Assert.Empty(region.Free);
        Assert.Empty(region.WayOut);
    }

    /// <summary>
    /// Walls standing on the lines between cells, the way walls snapped to floors do:
    /// round a room from -3 m to 3 m, between cells -7/-6 and 5/6 each way. The floor
    /// comes out to the walls' middles - the six-metre room takes its nine plates - which
    /// asking the cells alone could not give.
    /// </summary>
    [Fact]
    public void WallsOnTheLinesBetweenCellsCloseTheRoomAndTheFloorReachesThem()
    {
        static bool Shut(GridCell a, GridCell b)
        {
            bool Across(int x, int y) => (x == -7 && y == -6) || (x == -6 && y == -7) || (x == 5 && y == 6) || (x == 6 && y == 5);
            return a.J == b.J && Across(a.I, b.I) || a.I == b.I && Across(a.J, b.J);
        }

        var region = Geometry.FloodFloor(Plate, (i, j) => FloorCell.Free, Shut, 128, 12000);

        Assert.True(region.Closed);
        Assert.Equal(144, region.Free.Count);

        var big = new List<GridCell>();
        var small = new List<GridCell>();
        Geometry.FloorTiles(region.Free, big, small);
        Assert.Equal(9, big.Count);
        Assert.Empty(small);
    }

    [Fact]
    public void AShutPassageDoesNotHideTheCellBehindItFromAnotherSide()
    {
        // One passage shut, between (2, 0) and (3, 0); (3, 0) is reached round it.
        static bool Shut(GridCell a, GridCell b) =>
            (a.I == 2 && a.J == 0 && b.I == 3 && b.J == 0) || (a.I == 3 && a.J == 0 && b.I == 2 && b.J == 0);

        var region = Geometry.FloodFloor(Plate, Room(-6, 5, -6, 5), Shut, 128, 12000);

        Assert.True(region.Closed);
        Assert.Contains(Geometry.CellKey(3, 0), region.Free);
    }

    [Fact]
    public void APlateHalfOverAWallStartsFromItsOtherHalf()
    {
        // The plate's western column is wall; the rest of it starts the flood.
        var region = Geometry.FloodFloor(Plate, Room(-1, 5, -6, 5), null, 128, 12000);

        Assert.True(region.Closed);
        Assert.DoesNotContain(Geometry.CellKey(-2, 0), region.Free);
        Assert.Contains(Geometry.CellKey(-1, 0), region.Free);
    }
}

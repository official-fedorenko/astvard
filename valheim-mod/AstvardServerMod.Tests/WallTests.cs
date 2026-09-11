using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The wall round a floor: which edges of it get one, and how an edge is cut into the
/// widths the game's walls come in. What a player sees is walls standing on the floor's
/// outer edge, joints where the floor has its own, and no wall across a stairwell.
/// </summary>
public class WallTests
{
    // Cells are half a metre: a two-metre plate is four by four of them.
    private static HashSet<long> Cells(int i0, int j0, int i1, int j1)
    {
        var cells = new HashSet<long>();
        for (var i = i0; i < i1; i++)
            for (var j = j0; j < j1; j++)
                cells.Add(Geometry.CellKey(i, j));
        return cells;
    }

    [Fact]
    public void ASquareFloorHasFourSides()
    {
        // Four by four metres.
        var runs = Geometry.PerimeterRuns(Cells(0, 0, 8, 8));

        Assert.Equal(4, runs.Count);
        Assert.Contains(runs, r => r.ConstantI && r.Line == 0 && r.Outward == -1 && r.From == 0 && r.To == 8);
        Assert.Contains(runs, r => r.ConstantI && r.Line == 8 && r.Outward == 1 && r.From == 0 && r.To == 8);
        Assert.Contains(runs, r => !r.ConstantI && r.Line == 0 && r.Outward == -1 && r.From == 0 && r.To == 8);
        Assert.Contains(runs, r => !r.ConstantI && r.Line == 8 && r.Outward == 1 && r.From == 0 && r.To == 8);
    }

    [Fact]
    public void AnLShapedFloorHasSixSides()
    {
        var floor = Cells(0, 0, 8, 4);
        floor.UnionWith(Cells(0, 4, 4, 8));

        var runs = Geometry.PerimeterRuns(floor);

        Assert.Equal(6, runs.Count);
        // The inner corner: the top of the long arm, and the side of the short one.
        Assert.Contains(runs, r => !r.ConstantI && r.Line == 4 && r.Outward == 1 && r.From == 4 && r.To == 8);
        Assert.Contains(runs, r => r.ConstantI && r.Line == 4 && r.Outward == 1 && r.From == 4 && r.To == 8);
    }

    [Fact]
    public void AHoleInTheMiddleGetsNoWalls()
    {
        // A stairwell: one plate missing from the middle of a six-metre square.
        var floor = Cells(0, 0, 12, 12);
        floor.ExceptWith(Cells(4, 4, 8, 8));

        var runs = Geometry.PerimeterRuns(floor);

        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.True(r.Line == 0 || r.Line == 12, $"a run on line {r.Line}"));
    }

    [Fact]
    public void ANotchOpenToTheOutsideIsEdgeToo()
    {
        // The same missing plate, but on the edge: a porch cut into the square.
        var floor = Cells(0, 0, 12, 12);
        floor.ExceptWith(Cells(4, 8, 8, 12));

        var runs = Geometry.PerimeterRuns(floor);

        Assert.Equal(8, runs.Count);
        Assert.Contains(runs, r => !r.ConstantI && r.Line == 8 && r.Outward == 1 && r.From == 4 && r.To == 8);
    }

    [Fact]
    public void RunsComeOutInTheSameOrderEveryTime()
    {
        var floor = Cells(-4, -4, 4, 4);
        var first = Geometry.PerimeterRuns(floor);
        var again = Geometry.PerimeterRuns(new HashSet<long>(floor.Reverse()));

        Assert.Equal(first, again);
    }

    [Fact]
    public void ARunOnTheGridIsTwoMetrePanels()
    {
        var panels = Geometry.WallPanels(0, 16, out var gap);

        Assert.Equal(0, gap);
        Assert.Equal(new[] { 0, 4, 8, 12 }, panels.Select(p => p.Start));
        Assert.All(panels, p => Assert.Equal(4, p.Width));
    }

    [Fact]
    public void AnOddMetreGoesToAOneMetrePanel()
    {
        // Five metres from a joint: two, two and one.
        var panels = Geometry.WallPanels(0, 10, out var gap);

        Assert.Equal(0, gap);
        Assert.Equal(new[] { 4, 4, 2 }, panels.Select(p => p.Width));
    }

    [Fact]
    public void TwoMetrePanelsKeepToTheFloorsJoints()
    {
        // Starting a metre past a joint: one metre to reach it, then two-metre panels on it.
        var panels = Geometry.WallPanels(2, 16, out var gap);

        Assert.Equal(0, gap);
        Assert.Equal(new[] { 2, 4, 8, 12 }, panels.Select(p => p.Start));
        Assert.Equal(new[] { 2, 4, 4, 4 }, panels.Select(p => p.Width));
    }

    [Fact]
    public void ItWorksTheSameBelowZero()
    {
        var panels = Geometry.WallPanels(-6, 2, out var gap);

        Assert.Equal(0, gap);
        Assert.Equal(new[] { -6, -4, 0 }, panels.Select(p => p.Start));
        Assert.Equal(new[] { 2, 4, 2 }, panels.Select(p => p.Width));
    }

    [Fact]
    public void HalfAMetreOffTheGridStaysOpen()
    {
        // Laid off the metre grid by hand: the half-metre ends cannot take a wall.
        var panels = Geometry.WallPanels(1, 5, out var gap);

        Assert.Equal(2, gap);
        Assert.Equal(new[] { 2 }, panels.Select(p => p.Start));
    }

    [Fact]
    public void NothingForNothing()
    {
        Assert.Empty(Geometry.PerimeterRuns(new HashSet<long>()));
        Assert.Empty(Geometry.WallPanels(3, 3, out var gap));
        Assert.Equal(0, gap);
    }
}

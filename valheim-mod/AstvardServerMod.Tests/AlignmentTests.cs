using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// A build copied in game is never square to the world: the player was facing some
/// arbitrary direction, and standing on a step. Squaring it up is the arithmetic
/// that has gone wrong most often — once rotating a whole hall sideways, once
/// leaving a house floating 34 cm above the ground.
/// </summary>
public class AlignmentTests
{
    private static float[] BuildAtYaws(float baseYaw, params int[] quarterTurns)
    {
        var yaws = new float[quarterTurns.Length];
        for (var i = 0; i < quarterTurns.Length; i++)
        {
            var yaw = (baseYaw + quarterTurns[i] * 22.5f) % 360f;
            yaws[i] = yaw < 0f ? yaw + 360f : yaw;
        }
        return yaws;
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 180f)]
    [InlineData(0.7071068f, 0.7071068f, 90f)]
    [InlineData(-0.7071068f, 0.7071068f, 270f)]
    public void YawComesBackOutOfTheQuaternion(float qy, float qw, float expected)
    {
        Assert.Equal(expected, Geometry.YawOf(qy, qw), 2);
    }

    [Fact]
    public void ACleanBuildRecoversItsOwnBaseAngle()
    {
        var yaws = BuildAtYaws(7.3f, 0, 4, 8, 12, 0, 4);

        var found = Geometry.BestBaseYaw(yaws, 22.5f, 11.25f);
        Assert.Equal(7.3f, found, 1);
        Assert.True(Geometry.WorstYawError(yaws, found, 22.5f) < 0.05f);
    }

    /// <summary>
    /// The smelter hall was laid down at two facings 0.83 degrees apart. No single
    /// rotation squares both, and the fix is to accept that: pick the angle with the
    /// least total error and let each piece snap the rest of the way.
    /// </summary>
    [Fact]
    public void TwoFacingsSettleBetweenThemRatherThanOnOne()
    {
        var yaws = new List<float>();
        yaws.AddRange(BuildAtYaws(359.082f, 0, 4, 8, 12));
        yaws.AddRange(BuildAtYaws(359.914f, 0, 4));

        var found = Geometry.BestBaseYaw(yaws, 22.5f, 11.25f);
        var worst = Geometry.WorstYawError(yaws, found, 22.5f);

        // Nobody has to move further than the gap between the two families.
        Assert.True(worst <= 0.84f, $"worst correction was {worst:F3} deg");
    }

    /// <summary>
    /// Searching the whole circle finds angles a quarter-turn away that fit exactly
    /// as well — and silently turn the finished template sideways. The search stays
    /// near zero so the build keeps the facing it was made with.
    /// </summary>
    [Fact]
    public void TheSearchStaysNearZeroAndNeverTurnsTheBuild()
    {
        var yaws = BuildAtYaws(359.91f, 0, 4, 8, 12);

        var found = Geometry.BestBaseYaw(yaws, 22.5f, 11.25f);
        var fromZero = Math.Min(found, 360f - found);

        Assert.True(fromZero <= 11.25f, $"base drifted to {found:F2} deg");
    }

    [Fact]
    public void SnappingLandsOnTheGrid()
    {
        foreach (var yaw in new[] { 1.67f, 91.67f, 159.17f, 249.17f, 271.67f })
        {
            var snapped = Geometry.SnapYaw(yaw, 1.67f, 22.5f);
            Assert.Equal(0f, snapped % 22.5f, 3);
        }
    }

    [Fact]
    public void RotationRoundTrips()
    {
        var point = new Vec2(12.5f, -7.25f);
        var there = Geometry.RotateXZ(point, 37f);
        var back = Geometry.RotateXZ(there, -37f);

        Assert.Equal(point.X, back.X, 3);
        Assert.Equal(point.Z, back.Z, 3);
    }

    [Fact]
    public void GridShiftPutsScatteredCoordinatesBackOnTheGrid()
    {
        var values = new[] { 4.07f, 8.06f, 12.08f, 16.07f, 20.05f };

        var shift = Geometry.BestGridShift(values, 0.5f);
        foreach (var value in values)
        {
            var moved = value + shift;
            var off = Math.Abs(moved / 0.5f - MathF.Round(moved / 0.5f));
            Assert.True(off < 0.05f, $"{value} landed at {moved}");
        }
    }

    /// <summary>
    /// The case a plain average gets wrong: residuals sitting either side of a cell
    /// boundary average to the middle of the cell, which is the worst answer
    /// available. This is why the shift is chosen by least circular distance.
    /// </summary>
    [Fact]
    public void GridShiftSurvivesResidualsStraddlingTheBoundary()
    {
        var values = new[] { 4.01f, 8.49f, 12.02f, 16.48f, 20.01f };

        var shift = Geometry.BestGridShift(values, 0.5f);
        var onGrid = 0;
        foreach (var value in values)
        {
            var moved = value + shift;
            if (Math.Abs(moved / 0.5f - MathF.Round(moved / 0.5f)) < 0.06f) onGrid++;
        }

        Assert.True(onGrid >= 3, $"only {onGrid} of 5 landed on the grid");
    }

    [Fact]
    public void AVerticalOffsetIsFoundTheSameWay()
    {
        // The house copied while standing on a step: every level 34 cm high.
        var levels = new[] { 0.34f, 0.84f, 1.34f, 2.34f, 4.34f };

        var shift = Geometry.BestGridShift(levels, 0.5f);
        Assert.Equal(-0.34f, shift, 2);
    }
}

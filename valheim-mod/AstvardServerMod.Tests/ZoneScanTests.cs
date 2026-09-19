using System;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Радиус, с которым зону спрашивают у игры.
///
/// The zone itself is flat - Inside asks for x and z and nothing else - but the game hands
/// out pieces by a distance measured in three dimensions from a single point, and that
/// point stands at the player's height. So the radius has to be wide enough that the zone
/// still has some height left at its own edge; the flat reach leaves it none, which is how
/// chests on the floor above went unlabelled and unsorted with nothing said.
/// </summary>
public class ZoneScanTests
{
    private static Sorting.Zone Square(float radius) =>
        new Sorting.Zone { X = 0f, Z = 0f, Radius = radius, Square = true };

    private static Sorting.Zone Round(float radius) =>
        new Sorting.Zone { X = 0f, Z = 0f, Radius = radius, Square = false };

    /// <summary>How far above or below the player the scan still reaches, that far out.</summary>
    private static double HeightAt(float scanRadius, double flatDistance) =>
        Math.Sqrt(scanRadius * (double)scanRadius - flatDistance * flatDistance);

    [Fact]
    public void ACornerOfASquareKeepsItsHeadroom()
    {
        // The corner is the worst place in the zone: it is as far from the middle as the
        // scan used to reach, so a chest a step higher than the player fell out of it.
        var zone = Square(14f);
        var corner = 14.0 * Sorting.SquareDiagonal;

        Assert.True(Sorting.ScanRadius(zone) > corner);
        Assert.Equal(Sorting.Headroom, HeightAt(Sorting.ScanRadius(zone), corner), 1);
    }

    [Fact]
    public void TheEdgeOfACircleKeepsItToo()
    {
        var zone = Round(20f);

        Assert.Equal(Sorting.Headroom, HeightAt(Sorting.ScanRadius(zone), 20.0), 1);
    }

    [Fact]
    public void TheMiddleOfAZoneReachesFurtherUpThanItsEdge()
    {
        // Not a rule, a consequence worth stating: a sphere is deepest over its centre.
        // Chests right under the player were never the ones that went missing.
        var zone = Round(20f);

        Assert.True(HeightAt(Sorting.ScanRadius(zone), 0.0) > HeightAt(Sorting.ScanRadius(zone), 20.0));
    }

    [Fact]
    public void ARadiusBeyondTheLimitIsHeldToIt()
    {
        // The zone itself is clamped, so the scan has to be clamped with it - otherwise a
        // number typed with one zero too many would walk the whole world twice a second.
        var huge = Sorting.ScanRadius(Round(1000f));
        var limit = Sorting.ScanRadius(Round(Sorting.MaxZoneRadius));

        Assert.Equal(limit, huge, 3);
    }

    [Fact]
    public void NoZoneIsNoReach()
    {
        Assert.Equal(0f, Sorting.ScanRadius(null));
    }
}

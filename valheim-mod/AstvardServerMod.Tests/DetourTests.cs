using System;
using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// A road that meets an ore vein, a dolmen or a cave mouth goes round it instead of
/// paving over it — and comes back on its line, because the next piece of the road
/// starts where this one stopped.
/// </summary>
public class DetourTests
{
    private const float HalfWidth = 2f;

    private const float Clearance = 1f;

    private const float MinRamp = 5f;

    private const float RampPerStep = 3f;

    private const float MaxStep = 16f;

    /// <summary>A straight road along +X, a point every metre, so distance along is X.</summary>
    private static List<Vec2> Straight(float length)
    {
        var path = new List<Vec2>();
        for (var i = 0; i <= (int)length; i++) path.Add(new Vec2(i, 0f));
        return path;
    }

    private static Geometry.DetourPlan Round(List<Vec2> path, params Geometry.Blocker[] blockers)
    {
        return Geometry.Detour(path, blockers, HalfWidth, Clearance, MinRamp, RampPerStep, MaxStep);
    }

    /// <summary>How far the road ended up from something, at its nearest.</summary>
    private static float Clears(IList<Vec2> path, Geometry.Blocker blocker)
    {
        float distance;
        Geometry.NearestOnPath(path, blocker.At.X, blocker.At.Z, out distance);
        return distance;
    }

    [Fact]
    public void AnEmptyRoadIsLeftExactlyWhereItWas()
    {
        var path = Straight(100f);
        var plan = Round(path);

        Assert.Equal(0, plan.Taken);
        for (var i = 0; i < path.Count; i++)
        {
            Assert.Equal(path[i].X, plan.Path[i].X, 4);
            Assert.Equal(path[i].Z, plan.Path[i].Z, 4);
        }
    }

    [Fact]
    public void SomethingStandingClearOfTheRoadIsNotGoneRound()
    {
        // Three metres of paving and a metre of room: a vein twenty metres off the line
        // is nothing to do with this road.
        var plan = Round(Straight(100f), new Geometry.Blocker(new Vec2(50f, 20f), 2f));

        Assert.Empty(plan.Bends);
        Assert.Equal(0, plan.Taken);
    }

    [Fact]
    public void AVeinOnTheLineIsPassedWithTheRoadsOwnEdgeClearOfIt()
    {
        var blocker = new Geometry.Blocker(new Vec2(50f, 0f), 3f);
        var plan = Round(Straight(100f), blocker);

        Assert.Equal(1, plan.Taken);

        // Half the paving, the room asked for, and the vein itself: the paving does not
        // touch it, which is the whole point of going round.
        Assert.True(Clears(plan.Path, blocker) >= 3f + HalfWidth + Clearance - 0.05f,
            $"the road passed {Clears(plan.Path, blocker):F2} m from the middle of a 3 m vein");
    }

    [Theory]
    [InlineData(2f, -1)]    // a little to the left: duck right, that is the short way
    [InlineData(-2f, 1)]
    [InlineData(4f, -1)]
    [InlineData(-4f, 1)]
    public void TheRoadPassesOnWhicheverSideIsNearer(float standsAt, int expectedSign)
    {
        var blocker = new Geometry.Blocker(new Vec2(50f, standsAt), 2f);
        var plan = Round(Straight(100f), blocker);

        Assert.Equal(1, plan.Taken);
        Assert.Equal(expectedSign, Math.Sign(plan.Widest));

        // And it is the short way round: going the other way would have been further.
        var need = 2f + HalfWidth + Clearance;
        Assert.True(Math.Abs(plan.Widest) <= need - Math.Abs(standsAt) + 0.05f);
    }

    [Fact]
    public void BothEndsStayExactlyWhereTheyWere()
    {
        var path = Straight(100f);
        var plan = Round(path, new Geometry.Blocker(new Vec2(50f, 0f), 4f));

        // The piece laid after this one starts at this one's last point. A road that
        // came back a metre to the side would leave a step in the paving.
        Assert.Equal(path[0].Z, plan.Path[0].Z, 4);
        Assert.Equal(path[path.Count - 1].Z, plan.Path[path.Count - 1].Z, 4);
    }

    [Fact]
    public void TheEasingIsAsLongAsTheStepIsWideAndNeverShorterThanTheMinimum()
    {
        // A step of eight metres taken inside five would be two sixty degree turns.
        var wide = Round(Straight(200f), new Geometry.Blocker(new Vec2(100f, 0f), 5f));
        var bend = wide.Bends[0];
        var step = Math.Abs(bend.Step);

        Assert.True(step >= 8f);
        Assert.True(bend.To - bend.From >= 2f * RampPerStep * step,
            $"{step:F1} m aside inside {bend.To - bend.From:F1} m of road");

        // Whereas a step of a hand's width gets the minimum, not three times nothing.
        var slight = Round(Straight(200f), new Geometry.Blocker(new Vec2(100f, 2.9f), 0.2f));
        var slightBend = slight.Bends[0];
        Assert.True(Math.Abs(slightBend.Step) < MinRamp / RampPerStep);
        Assert.True(slightBend.To - slightBend.From >= 2f * MinRamp);
    }

    [Fact]
    public void TwoThingsCloseTogetherAreGoneRoundAsOne()
    {
        // Coming back on the line and leaving it again within a few metres reads as a
        // wobble, not as a way round.
        var first = new Geometry.Blocker(new Vec2(50f, 1f), 2f);
        var second = new Geometry.Blocker(new Vec2(58f, 1f), 2f);
        var plan = Round(Straight(120f), first, second);

        Assert.Single(plan.Bends);
        Assert.Equal(1, plan.Taken);
        Assert.True(Clears(plan.Path, first) >= 2f + HalfWidth + Clearance - 0.05f);
        Assert.True(Clears(plan.Path, second) >= 2f + HalfWidth + Clearance - 0.05f);
    }

    [Fact]
    public void TwoThingsOnOppositeSidesAreStillGoneRoundTogether()
    {
        // One step has to clear both, which means passing outside the further of them.
        var left = new Geometry.Blocker(new Vec2(50f, 2f), 2f);
        var right = new Geometry.Blocker(new Vec2(56f, -2f), 2f);
        var plan = Round(Straight(140f), left, right);

        Assert.Single(plan.Bends);
        Assert.True(Clears(plan.Path, left) >= 2f + HalfWidth + Clearance - 0.05f);
        Assert.True(Clears(plan.Path, right) >= 2f + HalfWidth + Clearance - 0.05f);
    }

    [Fact]
    public void TwoThingsFarApartGetABendEach()
    {
        var first = new Geometry.Blocker(new Vec2(50f, 0f), 2f);
        var second = new Geometry.Blocker(new Vec2(200f, 0f), 2f);
        var plan = Round(Straight(260f), first, second);

        Assert.Equal(2, plan.Bends.Count);
        Assert.Equal(2, plan.Taken);

        // And between them the road is back on its line.
        Assert.Equal(0f, plan.Path[125].Z, 3);
    }

    [Fact]
    public void SomethingTooBigToGetRoundIsLeftAloneAndSaidSo()
    {
        // A step wider than the road may stray would paint into zones nobody loaded.
        var plan = Round(Straight(300f), new Geometry.Blocker(new Vec2(150f, 0f), 40f));

        Assert.Equal(0, plan.Taken);
        Assert.Single(plan.Bends);
        Assert.Equal(Geometry.BendRefusal.TooWide, plan.Bends[0].Refused);
        Assert.Equal(0f, plan.Path[150].Z, 4);
    }

    [Fact]
    public void SomethingTooNearAnEndIsLeftForTheNextPiece()
    {
        // There is no room to ease out and back inside this stretch, and moving the end
        // is not allowed. The caller cuts the piece shorter and meets it again.
        var plan = Round(Straight(100f), new Geometry.Blocker(new Vec2(2f, 0f), 3f));

        Assert.Equal(0, plan.Taken);
        Assert.Single(plan.Bends);
        Assert.Equal(Geometry.BendRefusal.PastTheEnd, plan.Bends[0].Refused);
        Assert.Equal(0f, plan.Path[0].Z, 4);
    }

    [Fact]
    public void TheBendIsSmoothEnoughToBeARoad()
    {
        // No point turns much more sharply than the easing promises: this is what keeps
        // the smoothing that follows from making a corner of it.
        var plan = Round(Straight(200f), new Geometry.Blocker(new Vec2(100f, 0f), 4f));

        var worst = 0f;
        for (var i = 1; i < plan.Path.Count; i++)
        {
            var sideways = Math.Abs(plan.Path[i].Z - plan.Path[i - 1].Z);
            if (sideways > worst) worst = sideways;
        }

        // Half a metre sideways to a metre along at the sharpest, about twenty-seven
        // degrees off the line, and only at the middle of the easing where the eye
        // expects a road to be turning. A dogleg would be the whole step in one metre.
        Assert.True(worst < 0.51f, $"the sharpest metre moved {worst:F2} m sideways");
    }

    [Fact]
    public void ARoadThatBendsIsStillTheSameNumberOfPoints()
    {
        // Everything downstream — the clearing, the levelling, the paint, the torches —
        // is handed this list and counts on it.
        var path = Straight(100f);
        var plan = Round(path, new Geometry.Blocker(new Vec2(50f, 0f), 3f));
        Assert.Equal(path.Count, plan.Path.Count);
    }
}

/// <summary>
/// A road follows the land, and the land is sometimes too steep to walk up. The
/// profile is held to a grade, as far as the digging budget allows it to be.
/// </summary>
public class GradeTests
{
    private static float[] Flat(int n, float slope)
    {
        var heights = new float[n];
        for (var i = 0; i < n; i++) heights[i] = i * slope;
        return heights;
    }

    [Fact]
    public void AGradeAlreadyGentleEnoughIsLeftAlone()
    {
        var heights = Flat(40, 0.2f);
        var held = Geometry.LimitGrade(heights, 1f, 0.35f, 6f);

        for (var i = 0; i < heights.Length; i++) Assert.Equal(heights[i], held[i], 3);
    }

    [Fact]
    public void AStepIsCutAndFilledIntoARamp()
    {
        var heights = new float[21];
        for (var i = 10; i < 21; i++) heights[i] = 10f;

        var held = Geometry.LimitGrade(heights, 1f, 0.35f, 6f);

        // Ten metres over twenty is half a metre a metre whatever we do — both ends are
        // the ground and cannot move. What it must not be any more is ten metres in one
        // step.
        Assert.True(Geometry.Steepest(heights, 1f) > 9f);
        Assert.True(Geometry.Steepest(held, 1f) < 0.6f,
            $"still {Geometry.Steepest(held, 1f):F2} steep");
    }

    [Fact]
    public void TheEndsStayOnTheGround()
    {
        var heights = new float[21];
        for (var i = 10; i < 21; i++) heights[i] = 10f;

        var held = Geometry.LimitGrade(heights, 1f, 0.35f, 6f);

        // The road meets the land where it starts, and the next piece starts here.
        Assert.Equal(heights[0], held[0], 3);
        Assert.Equal(heights[20], held[20], 3);
    }

    [Fact]
    public void TheDiggingBudgetIsNeverExceeded()
    {
        // Past eight metres the engine stops listening and leaves a wall where the
        // asking stopped, so the asking has to stop first.
        var heights = new float[41];
        for (var i = 20; i < 41; i++) heights[i] = 30f;

        var held = Geometry.LimitGrade(heights, 1f, 0.2f, 6f);

        for (var i = 0; i < heights.Length; i++)
            Assert.True(Math.Abs(held[i] - heights[i]) <= 6.001f,
                $"point {i} asked for {Math.Abs(held[i] - heights[i]):F2} m of digging");
    }

    [Fact]
    public void AHillsideTooSteepToPayForComesOutGentlerThanItWas()
    {
        // Not walkable, but no longer a cliff — and the caller is told which happened
        // by asking Steepest, not by being promised anything here.
        var heights = new float[31];
        for (var i = 0; i < 31; i++) heights[i] = i < 15 ? 0f : 20f;

        var held = Geometry.LimitGrade(heights, 1f, 0.2f, 2f);

        Assert.True(Geometry.Steepest(held, 1f) < Geometry.Steepest(heights, 1f));
        Assert.True(Geometry.Steepest(held, 1f) > 0.2f);
    }
}

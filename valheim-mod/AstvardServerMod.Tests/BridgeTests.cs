using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Where a wooden bridge puts its legs. The limits these lean on came out of
/// WearNTear's own support decay, so the numbers are not adjustable taste: a leg
/// one step too far apart is a bridge that falls down some seconds after it is
/// finished, with nothing to show why.
/// </summary>
public class BridgeTests
{
    /// <summary>
    /// A profile of the given depths below a deck at zero.
    /// </summary>
    private static float[] Under(params float[] depths)
    {
        var ground = new float[depths.Length];
        for (var i = 0; i < depths.Length; i++) ground[i] = -depths[i];
        return ground;
    }

    [Theory]
    [InlineData(0f, 16f)]      // on the bank: full support, the free span
    [InlineData(2f, 16f)]
    [InlineData(4f, 14f)]
    [InlineData(6f, 10f)]
    [InlineData(8f, 10f)]
    [InlineData(10f, 6f)]
    [InlineData(12f, 4f)]
    [InlineData(14f, 2f)]
    public void SpacingMatchesWhatTheSupportDecayAllows(float height, float span)
    {
        Assert.Equal(span, Geometry.MaxPierSpacing(height));
    }

    [Fact]
    public void HeightsRoundUpToTheNextMeasuredStep()
    {
        // Between two measured heights the shorter span is the honest answer.
        // Interpolating would promise a distance nothing was ever tested at.
        Assert.Equal(14f, Geometry.MaxPierSpacing(2.1f));
        Assert.Equal(10f, Geometry.MaxPierSpacing(5.9f));
        Assert.Equal(2f, Geometry.MaxPierSpacing(13.5f));
    }

    [Fact]
    public void NothingHangsOffALegTallerThanTheLimit()
    {
        Assert.Equal(0f, Geometry.MaxPierSpacing(Geometry.MaxPierHeight + 0.1f));
        Assert.Equal(0f, Geometry.MaxPierSpacing(20f));
    }

    [Fact]
    public void AGapInsideTheFreeSpanNeedsNoLegs()
    {
        // 16 m of deck between two banks, which is exactly what the two-sided rule
        // carries, over a ravine too deep to stand anything in. Water a leg could
        // reach would get one: the spacing preference plants legs wherever it can,
        // and only the free span is left when it cannot.
        var ground = Under(0f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        Assert.Empty(plan.Piers);
    }

    [Fact]
    public void AGapPastTheFreeSpanWithNowhereToStandFails()
    {
        // Same ravine, one sample wider, and too deep for a leg anywhere in it.
        var ground = Under(0f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.False(plan.Stands);
        Assert.Equal(0, plan.GapFrom);
        Assert.Equal(ground.Length - 1, plan.GapTo);
    }

    /// <summary>
    /// What the refusal points at. The far bank is where the bridge ends, not where
    /// the trouble is, and naming it makes one hole in an otherwise fine crossing read
    /// as the whole crossing being impossible.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheStretchThatCannotBeCrossed()
    {
        // Twenty metres of water too deep for any leg, with ordinary ground on both
        // sides of it. The walk gets as far as sample 1 and stops; sample 11 is the
        // first place past the hole where a leg could stand again.
        var ground = Under(0f, 2f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 2f, 2f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.False(plan.Stands);
        Assert.Equal(1, plan.GapFrom);
        Assert.Equal(11, plan.GapTo);
    }

    [Fact]
    public void TheBanksAreNeverListedAsLegs()
    {
        // They carry the deck like any other support, but they are the ends of the
        // bridge rather than something it has to build.
        var ground = Under(new float[25].Select(_ => 2f).ToArray());
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        Assert.DoesNotContain(0, plan.Piers);
        Assert.DoesNotContain(ground.Length - 1, plan.Piers);
    }

    /// <summary>
    /// Strength is not the only thing deciding where legs go. Shallow water lets them
    /// stand sixteen metres apart and still hold, which builds a deck with almost
    /// nothing under it — so the walk keeps to a rhythm when it has the choice.
    /// </summary>
    [Fact]
    public void LegsKeepToTheirRhythmWhereStrengthWouldAllowMore()
    {
        // Two metres deep the whole way, where the support rules alone permit eight
        // sections between legs.
        var ground = Under(new float[41].Select(_ => 2f).ToArray());
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);

        var supports = new List<int> { 0 };
        supports.AddRange(plan.Piers);
        supports.Add(ground.Length - 1);

        for (var i = 1; i < supports.Count; i++)
            Assert.True((supports[i] - supports[i - 1]) * 2f <= Geometry.MaxMetresBetweenLegs,
                $"legs at {supports[i - 1]} and {supports[i]} are further apart than asked");
    }

    /// <summary>
    /// The rhythm is a preference and has to give way. A stretch too deep to stand a leg
    /// in is exactly the case the support rules exist for, and holding out for a tidy
    /// spacing there would refuse a crossing that stands.
    /// </summary>
    [Fact]
    public void TheRhythmGivesWayRatherThanRefusingACrossing()
    {
        // Twelve metres of water too deep for any leg, inside the free span, with
        // ordinary ground either side.
        var ground = Under(0f, 2f, 20f, 20f, 20f, 20f, 20f, 2f, 2f, 2f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);

        // And the stretch it had to reach across is longer than the preference.
        var supports = new List<int> { 0 };
        supports.AddRange(plan.Piers);
        supports.Add(ground.Length - 1);
        Assert.Contains(supports.Skip(1).Select((v, i) => (v - supports[i]) * 2f),
                        gap => gap > Geometry.MaxMetresBetweenLegs);
    }

    [Fact]
    public void DeeperWaterGetsMoreLegs()
    {
        var shallow = Geometry.PlanPiers(Under(new float[41].Select(_ => 3f).ToArray()), 2f, 0f);
        var deep = Geometry.PlanPiers(Under(new float[41].Select(_ => 11f).ToArray()), 2f, 0f);

        Assert.True(shallow.Stands);
        Assert.True(deep.Stands);
        Assert.True(deep.Piers.Count > shallow.Piers.Count,
            $"deep {deep.Piers.Count} should need more legs than shallow {shallow.Piers.Count}");
    }

    [Fact]
    public void NoLegIsEverPlantedDeeperThanItCanStand()
    {
        // A channel dropping past the limit in the middle and coming back.
        var ground = Under(0f, 4f, 9f, 13f, 18f, 22f, 18f, 13f, 9f, 4f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        foreach (var pier in plan.Piers)
            Assert.True(-ground[pier] <= Geometry.MaxPierHeight,
                $"leg at sample {pier} would be {-ground[pier]} m tall");
    }

    /// <summary>
    /// The walk has to try every candidate rather than stopping at the first one it
    /// cannot reach. A shallow spot further out carries a longer stretch than a deep
    /// one nearer, so a walk that gave up at the first refusal would plant legs it did
    /// not need — or call an ordinary crossing impossible.
    /// </summary>
    [Fact]
    public void ADistantShallowSpotIsReachedPastNearerDeepOnes()
    {
        // Samples 1 and 2 are twenty metres down, past what any wooden leg can stand,
        // so the walk has to look past both of them to sample 3. Stopping at the first
        // candidate it cannot use would call this crossing impossible.
        var ground = Under(0f, 20f, 20f, 2f, 20f, 20f, 2f, 2f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        Assert.Contains(3, plan.Piers);
        Assert.DoesNotContain(1, plan.Piers);
        Assert.DoesNotContain(2, plan.Piers);
    }

    [Fact]
    public void GroundStandingAboveTheDeckIsNotANegativeLeg()
    {
        // The deck cuts into a rise: that sample is resting on the ground, which is
        // the strongest support there is, not a leg of negative height.
        Assert.Equal(Geometry.MaxFreeSpan, Geometry.MaxPierSpacing(0f));

        var ground = Under(0f, 2f, -5f, 2f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);
        Assert.True(plan.Stands);
    }

    [Fact]
    public void ATrivialProfileIsNotAFailure()
    {
        Assert.True(Geometry.PlanPiers(new[] { 0f, 0f }, 2f, 0f).Stands);
        Assert.False(Geometry.PlanPiers(null, 2f, 0f).Stands);
    }
}

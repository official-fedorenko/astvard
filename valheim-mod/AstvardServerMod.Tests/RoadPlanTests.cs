using System;
using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Дорога ищется по цене земли, а не рисуется кривой: она обходит деревню за сто метров
/// до неё, потому что так дешевле, а не потому, что кто-то посчитал отступ.
/// </summary>
public class RoadPlanTests
{
    private const float Step = 8f;

    /// <summary>Поле в метрах вокруг нуля: клетки по 8 м, середина в (0, 0).</summary>
    private static RoadPlan.Field Ground(float reach)
    {
        var side = (int)(reach * 2 / Step) + 1;
        var half = side / 2;
        return new RoadPlan.Field(side, side, Step, new Vec2(-half * Step, -half * Step));
    }

    private static float Nearest(IList<Vec2> path, Vec2 at)
    {
        var best = float.MaxValue;
        foreach (var point in path)
        {
            var dx = point.X - at.X;
            var dz = point.Z - at.Z;
            var away = (float)Math.Sqrt(dx * dx + dz * dz);
            if (away < best) best = away;
        }

        return best;
    }

    [Fact]
    public void AnEmptyPlainIsCrossedStraight()
    {
        var field = Ground(200f);
        var route = RoadPlan.Find(field, new Vec2(-160f, 0f), new Vec2(160f, 0f));

        Assert.True(route.Found, route.Why);

        // По прямой 320 м; ступеньки сетки не должны прибавить и процента.
        Assert.True(route.Metres < 324f, $"вышло {route.Metres:F0} м вместо 320");
    }

    [Fact]
    public void AVillageIsGoneRoundWithoutAnybodyMeasuringTheStep()
    {
        // Это и есть причина всей затеи: деревня в 35 м радиусом на прямой между метками.
        // Прежней укладке такой обход был не по силам - разгон вшестеро длиннее отступа, а
        // кусок дороги 91 м, - и восемь укладок подряд дорога шла сквозь неё.
        var field = Ground(300f);
        var village = new Vec2(0f, 0f);
        field.Circle(village, 35f, RoadPlan.Blocked);

        var route = RoadPlan.Find(field, new Vec2(-240f, 0f), new Vec2(240f, 0f));

        Assert.True(route.Found, route.Why);
        Assert.True(Nearest(route.Path, village) >= 35f - Step,
            $"дорога подошла к деревне на {Nearest(route.Path, village):F1} м");
    }

    [Fact]
    public void ExpensiveGroundIsAvoidedWhenGoingRoundIsCheaper()
    {
        // Болото поперёк пути: пройти можно, но втридорога. Дорога должна его обогнуть,
        // и это ровно то, чем уклон отличается от жилы - он дорог, а не непроходим.
        var field = Ground(300f);
        for (var z = -4; z <= 4; z++)
        for (var x = -2; x <= 2; x++)
            field[field.Wide / 2 + x, field.High / 2 + z] = 40f;

        var route = RoadPlan.Find(field, new Vec2(-200f, 0f), new Vec2(200f, 0f));

        Assert.True(route.Found, route.Why);
        Assert.True(route.Worst < 40f, $"дорога пошла по дорогому, худшая клетка {route.Worst}");
    }

    [Fact]
    public void ExpensiveGroundIsTakenWhenGoingRoundIsDearer()
    {
        // И обратное: если дорогое не обойти, по нему идут. Инструмент, который
        // отказывается платить никогда, однажды не найдёт пути вовсе.
        var field = Ground(120f);
        for (var z = 0; z < field.High; z++)
            field[field.Wide / 2, z] = 6f;

        var route = RoadPlan.Find(field, new Vec2(-80f, 0f), new Vec2(80f, 0f));

        Assert.True(route.Found, route.Why);
        Assert.Equal(6f, route.Worst, 3);
    }

    [Fact]
    public void AWallWithNoDoorIsSaidToHaveNoDoor()
    {
        var field = Ground(120f);
        for (var z = 0; z < field.High; z++)
            field[field.Wide / 2, z] = RoadPlan.Blocked;

        var route = RoadPlan.Find(field, new Vec2(-80f, 0f), new Vec2(80f, 0f));

        Assert.False(route.Found);
        Assert.Equal("прохода нет", route.Why);
    }

    [Fact]
    public void TwoBlockedCornersLeaveNoDiagonalToSqueezeThrough()
    {
        // Щель между двумя непроходимыми клетками по диагонали - это не проход: дорога
        // шириной четыре метра в угол не пролезает, как бы ни считала сетка.
        var field = Ground(60f);
        var mid = field.Wide / 2;
        for (var z = 0; z < field.High; z++)
            if (z != mid) field[mid, z] = RoadPlan.Blocked;
        field[mid, mid] = RoadPlan.Blocked;
        field[mid - 1, mid] = RoadPlan.Blocked;
        field[mid + 1, mid] = RoadPlan.Blocked;

        var route = RoadPlan.Find(field, new Vec2(-40f, 0f), new Vec2(40f, 0f));

        Assert.False(route.Found);
    }

    [Fact]
    public void ACircleNeverMakesBlockedGroundPassable()
    {
        // Круг гнезда, наложенный на воду, не делает воду проходимой: цена только растёт.
        var field = Ground(60f);
        field[3, 3] = RoadPlan.Blocked;
        field.Circle(field.World(3, 3), 12f, 5f);

        Assert.Equal(RoadPlan.Blocked, field[3, 3]);
    }

    [Fact]
    public void TheEndsSurviveThinning()
    {
        var path = new List<Vec2>();
        for (var i = 0; i <= 100; i++) path.Add(new Vec2(i, 0f));

        var thin = RoadPlan.Simplify(path, 0.5f);

        Assert.Equal(2, thin.Count);
        Assert.Equal(0f, thin[0].X, 3);
        Assert.Equal(100f, thin[thin.Count - 1].X, 3);
    }

    [Fact]
    public void ThinningKeepsTheCornerThatMatters()
    {
        var path = new List<Vec2>();
        for (var i = 0; i <= 50; i++) path.Add(new Vec2(i, 0f));
        for (var i = 1; i <= 50; i++) path.Add(new Vec2(50f, i));

        var thin = RoadPlan.Simplify(path, 0.5f);

        Assert.Equal(3, thin.Count);
        Assert.Equal(50f, thin[1].X, 3);
        Assert.Equal(0f, thin[1].Z, 3);
    }

    [Fact]
    public void RoundingLeavesBothEndsWhereTheyWere()
    {
        // Концы дороги - это середины меток, и двигать их поиску нечем.
        var path = new List<Vec2>
        {
            new Vec2(0f, 0f), new Vec2(40f, 0f), new Vec2(40f, 40f), new Vec2(80f, 40f),
        };

        var round = RoadPlan.Round(path, 4, 0.5f);

        Assert.Equal(path[0].X, round[0].X, 3);
        Assert.Equal(path[0].Z, round[0].Z, 3);
        Assert.Equal(path[3].X, round[3].X, 3);
        Assert.Equal(path[3].Z, round[3].Z, 3);
    }

    [Fact]
    public void RoundingTakesTheCornerOff()
    {
        var path = new List<Vec2> { new Vec2(0f, 0f), new Vec2(40f, 0f), new Vec2(40f, 40f) };

        var round = RoadPlan.Round(path, 6, 0.5f);

        // Угол уехал к середине между соседями, то есть срезался.
        Assert.True(round[1].X < 39f && round[1].Z > 1f,
            $"угол остался на {round[1].X:F1} {round[1].Z:F1}");
    }

    [Fact]
    public void WalkingPutsAPointEveryStepAndKeepsTheEnd()
    {
        var path = new List<Vec2> { new Vec2(0f, 0f), new Vec2(10.5f, 0f) };

        var walked = RoadPlan.Walk(path, 1f);

        Assert.Equal(12, walked.Count);
        Assert.Equal(0f, walked[0].X, 3);
        Assert.Equal(10.5f, walked[walked.Count - 1].X, 3);
        for (var i = 1; i < 11; i++) Assert.Equal(i, walked[i].X, 3);
    }

    [Fact]
    public void WalkingRoundsACornerWithoutLosingLength()
    {
        var path = new List<Vec2> { new Vec2(0f, 0f), new Vec2(30f, 0f), new Vec2(30f, 30f) };

        var walked = RoadPlan.Walk(path, 1f);

        Assert.Equal(60f, RoadPlan.Length(walked), 1);
    }

    [Fact]
    public void AFieldOfWaterAloneIsNoField()
    {
        var field = Ground(40f);
        for (var i = 0; i < field.Cost.Length; i++) field.Cost[i] = RoadPlan.Blocked;

        Assert.False(field.AnyOpen());
    }
}

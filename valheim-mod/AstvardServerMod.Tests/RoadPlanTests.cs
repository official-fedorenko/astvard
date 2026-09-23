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

    /// <summary>Те же, что у прокладки: проверять надо на том, чем кладут.</summary>
    private const int Passes = 600;

    private const float Pull = 0.5f;

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

    /// <summary>Ни одной точки в непроходимой клетке — то самое, что обещает сглаживание.</summary>
    private static bool Clear(RoadPlan.Field field, IList<Vec2> path)
    {
        foreach (var point in path)
        {
            int x, z;
            if (!field.Cell(point, out x, out z)) return false;
            if (field[x, z] >= RoadPlan.Blocked) return false;
        }

        return true;
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
    public void SmoothingRoundsTheCornerAndLeavesTheEndsAlone()
    {
        // Концы дороги - это середины меток, и двигать их сглаживанию нечем. А угол
        // между ними обязан стать дугой: излом на стыке с кругом хозяин увидел в игре
        // первым же вечером - «от круга бывают сильно резкие дорожки».
        var field = Ground(200f);
        var corner = new Vec2(0f, 0f);
        var walk = RoadPlan.Walk(
            new List<Vec2> { new Vec2(-60f, 0f), corner, new Vec2(0f, 60f) }, 1f);

        var easy = RoadPlan.Ease(field, walk, Passes, Pull);

        Assert.Equal(walk[0].X, easy[0].X, 3);
        Assert.Equal(walk[0].Z, easy[0].Z, 3);
        Assert.Equal(walk[walk.Count - 1].X, easy[easy.Count - 1].X, 3);
        Assert.Equal(walk[walk.Count - 1].Z, easy[easy.Count - 1].Z, 3);

        // Срез прямого угла - около 9,7 м, то есть дуга радиусом метров двадцать пять.
        // Прежние три прохода по редкой ломаной оставляли здесь ноль.
        Assert.True(Nearest(easy, corner) > 8f,
            $"угол остался изломом: до него {Nearest(easy, corner):F1} м");
        Assert.True(RoadPlan.Length(easy) < RoadPlan.Length(walk) - 5f,
            $"дорога не срезала угла: было {RoadPlan.Length(walk):F0}, стало {RoadPlan.Length(easy):F0}");
    }

    [Fact]
    public void SmoothingNeverCutsThroughWhatTheSearchWentRound()
    {
        // Сглаживание тянет путь к хорде, то есть срезает углы, - а срезанный угол у
        // валуна это дорога сквозь валун. Обход, найденный поиском, оно портить не вправе.
        var walk = RoadPlan.Walk(
            new List<Vec2> { new Vec2(-20f, 0f), new Vec2(0f, -30f), new Vec2(20f, 0f) }, 1f);

        var field = Ground(200f);
        field.Circle(new Vec2(0f, 0f), 12f, RoadPlan.Blocked);

        Assert.True(Clear(field, walk), "сам обход задевает валун — проверять было бы нечего");
        Assert.True(Clear(field, RoadPlan.Ease(field, walk, Passes, Pull)),
            "сглаживание срезало угол и завело дорогу в валун");

        // И проверка не пуста: без валуна то же сглаживание идёт ровно в те клетки.
        Assert.False(Clear(field, RoadPlan.Ease(Ground(200f), walk, Passes, Pull)),
            "без валуна сглаживание туда и не шло — проверять было нечего");
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

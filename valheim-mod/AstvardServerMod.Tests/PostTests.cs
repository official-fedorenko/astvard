using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Where the torches along a road stand. What a player sees is whether they come at an
/// even step whatever the road does, stand just off its edge on the side they should,
/// and leave neither end of the road dark.
/// </summary>
public class PostTests
{
    private static List<Vec2> Straight(float length)
    {
        return Geometry.Bezier(new Vec2(0f, 0f), new Vec2(length, 0f), 0f, 1f);
    }

    [Fact]
    public void AStraightRoadGetsAPairEveryStepFromEndToEnd()
    {
        var posts = Geometry.EdgePosts(Straight(40f), 10f, 2f);

        Assert.Equal(10, posts.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i * 10f, posts[2 * i].At.X, 3);
            Assert.Equal(i * 10f, posts[2 * i + 1].At.X, 3);
            Assert.Equal(2f, posts[2 * i].At.Z, 3);
            Assert.Equal(-2f, posts[2 * i + 1].At.Z, 3);
        }
    }

    [Fact]
    public void WhatTheStepDoesNotDivideIsSharedBetweenTheEnds()
    {
        // 47 m takes four whole steps; the 7 m left over go half to each end.
        var posts = Geometry.EdgePosts(Straight(47f), 10f, 2f);

        Assert.Equal(10, posts.Count);
        Assert.Equal(3.5f, posts[0].At.X, 3);
        Assert.Equal(43.5f, posts[^1].At.X, 3);
    }

    [Fact]
    public void ARoadShorterThanAStepGetsOnePairInItsMiddle()
    {
        var posts = Geometry.EdgePosts(Straight(6f), 10f, 1.5f);

        Assert.Equal(2, posts.Count);
        Assert.Equal(3f, posts[0].At.X, 3);
        Assert.Equal(3f, posts[1].At.X, 3);
    }

    [Fact]
    public void TheFirstOfEachPairIsOnTheLeftOfTheTravel()
    {
        // Heading north, left is west - the same side a positive curve bows to.
        var north = Geometry.Bezier(new Vec2(5f, 0f), new Vec2(5f, 30f), 0f, 1f);
        var posts = Geometry.EdgePosts(north, 10f, 2f);

        Assert.All(Enumerable.Range(0, posts.Count / 2), i =>
        {
            Assert.Equal(3f, posts[2 * i].At.X, 3);
            Assert.Equal(7f, posts[2 * i + 1].At.X, 3);
        });
    }

    [Fact]
    public void OnABendEveryPostStandsTheSameWayOffTheRoad()
    {
        var bend = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(100f, 0f), 12f, 1f);
        var posts = Geometry.EdgePosts(bend, 10f, 2.6f);

        Assert.NotEmpty(posts);
        foreach (var post in posts)
        {
            var off = Geometry.DistanceToPath(bend, 0, bend.Count - 1, post.At.X, post.At.Z);
            Assert.True(Math.Abs(off - 2.6f) < 0.05f, $"a post stands {off:F2} m off the road");
        }
    }

    [Fact]
    public void OnABendTheStepIsCountedAlongTheRoad()
    {
        // The curve's samples bunch up and spread out, so a step counted in samples
        // would come out uneven. Measured along the road it stays the same; the
        // straight line between two neighbours can only come out a little shorter.
        var bend = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(100f, 0f), 12f, 1f);
        var posts = Geometry.EdgePosts(bend, 10f, 0f);

        for (var i = 2; i < posts.Count; i += 2)
        {
            var dx = posts[i].At.X - posts[i - 2].At.X;
            var dz = posts[i].At.Z - posts[i - 2].At.Z;
            var chord = (float)Math.Sqrt(dx * dx + dz * dz);
            Assert.InRange(chord, 9.7f, 10.001f);
        }
    }

    [Fact]
    public void ThePostsKnowWhichWayTheRoadRuns()
    {
        var posts = Geometry.EdgePosts(Straight(20f), 10f, 2f);

        Assert.All(posts, post =>
        {
            Assert.Equal(1f, post.Along.X, 3);
            Assert.Equal(0f, post.Along.Z, 3);
        });
    }

    [Fact]
    public void NoPathNoPosts()
    {
        Assert.Empty(Geometry.EdgePosts(new List<Vec2>(), 10f, 2f));
        Assert.Empty(Geometry.EdgePosts(new List<Vec2> { new(3f, 4f) }, 10f, 2f));
        Assert.Empty(Geometry.EdgePosts(new List<Vec2> { new(3f, 4f), new(3f, 4f) }, 10f, 2f));
    }

    [Fact]
    public void ARingPutsItsPostsEvenlyOnTheRim()
    {
        var centre = new Vec2(10f, -5f);
        var posts = Geometry.RingPosts(centre, 8f, 10f);

        // 2 pi 8 is about 50 m: five posts ten metres apart.
        Assert.Equal(5, posts.Count);
        Assert.All(posts, post =>
        {
            var dx = post.At.X - centre.X;
            var dz = post.At.Z - centre.Z;
            Assert.Equal(8f, (float)Math.Sqrt(dx * dx + dz * dz), 3);
            // The tangent is at right angles to the radius.
            Assert.Equal(0f, dx * post.Along.X + dz * post.Along.Z, 3);
        });
    }

    [Fact]
    public void ASmallPadStillGetsThree()
    {
        Assert.Equal(3, Geometry.RingPosts(new Vec2(0f, 0f), 2f, 10f).Count);
        Assert.Empty(Geometry.RingPosts(new Vec2(0f, 0f), 0f, 10f));
    }
}

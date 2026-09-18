using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Зона, у которой есть хозяин и вписанные.
///
/// The zone moved to the server, and with it the question of who may do what to it. The
/// answers live here because everything else about them needs a server, a client and two
/// people standing in a base - and because getting them wrong hands somebody the keys to
/// another man's storage.
/// </summary>
public class SharedZoneTests
{
    private const string Mine = "76561198425108760";

    private const string Friend = "76561198000000001";

    private static Sorting.Zone Zone(string owner, params string[] members)
    {
        return new Sorting.Zone
        {
            X = 10f, Z = 20f, Radius = 16f, Square = true, Angle = 45f, Name = "Двор",
            Id = 7, Owner = owner, Members = new List<string>(members),
        };
    }

    [Theory]
    [InlineData("76561198425108760", "76561198425108760")]
    [InlineData("V_76561198425108760", "76561198425108760")]
    [InlineData("Steam_76561198425108760", "76561198425108760")]
    [InlineData("", "")]
    [InlineData("нет тут цифр", "")]
    public void AnIdIsDigitsAndNothingElse(string given, string expected)
    {
        // Every prefix the game puts on an id washes out the same way, so a zone written
        // with one form is still that person's when the other turns up.
        Assert.Equal(expected, Sorting.CleanId(given));
    }

    [Fact]
    public void AnIdCannotTearTheRecordInTwo()
    {
        // The id rides in the same line as the zone: a comma in it would end the field and
        // a semicolon would end the zone, taking every zone after it down with the parse.
        Assert.Equal("123456", Sorting.CleanId("12,34;56"));
    }

    [Fact]
    public void TheOwnerAndThePeopleWrittenInSeeTheZone()
    {
        var zone = Zone(Mine, Friend);

        Assert.True(Sorting.Sees(zone, Mine));
        Assert.True(Sorting.Sees(zone, Friend));
        Assert.True(Sorting.Sees(zone, "V_" + Friend));
        Assert.False(Sorting.Sees(zone, "76561198999999999"));
        Assert.False(Sorting.Sees(zone, ""));
    }

    [Fact]
    public void BeingWrittenInIsLeaveToWorkAndNotToMove()
    {
        var zone = Zone(Mine, Friend);

        Assert.True(Sorting.MayEdit(zone, Mine, false));
        Assert.False(Sorting.MayEdit(zone, Friend, false));
        Assert.True(Sorting.MayEdit(zone, Friend, true));      // an admin may
        Assert.False(Sorting.MayEdit(zone, "", false));
    }

    [Fact]
    public void AZoneWithNoOwnerIsNobodysButThisClients()
    {
        // What every zone written before the server kept them looks like. No owner means no
        // one to check against, and MayEdit says no to everybody rather than yes.
        var zone = Zone("");

        Assert.False(Sorting.Sees(zone, Mine));
        Assert.False(Sorting.MayEdit(zone, Mine, false));
    }

    [Fact]
    public void AZoneSurvivesTheRoundTrip()
    {
        var zone = Zone(Mine, Friend);
        zone.Drive = true;

        var back = Assert.Single(Sorting.Parse(Sorting.Pack(new[] { zone })));

        Assert.Equal(7, back.Id);
        Assert.Equal(Mine, back.Owner);
        Assert.Equal(new List<string> { Friend }, back.Members);
        Assert.True(back.Drive);
        Assert.Equal("Двор", back.Name);
        Assert.True(back.Square);
        Assert.Equal(45f, back.Angle, 1);
        Assert.Equal(16f, back.Radius, 1);
    }

    [Fact]
    public void ARecordFromBeforeAllThisStillReads()
    {
        // Six fields is what a zone looked like when it lived in one man's config, and his
        // config still holds them: they have to read as his own, unshared, unnumbered.
        var back = Assert.Single(Sorting.Parse("10.0,20.0,16.0,1,45.0,Двор"));

        Assert.Equal(0, back.Id);
        Assert.Equal("", back.Owner);
        Assert.Empty(back.Members);
        Assert.False(back.Drive);
        Assert.Equal("Двор", back.Name);
    }

    [Fact]
    public void TheWrittenInAreKeptOnceEach()
    {
        var members = Sorting.ParseMembers($"{Mine} {Friend} {Mine}  V_{Friend}");

        Assert.Equal(new List<string> { Mine, Friend }, members);
    }

    [Fact]
    public void ThereIsAnEndToHowManyCanBeWrittenIn()
    {
        var many = new List<string>();
        for (var i = 0; i < Sorting.MaxMembers + 10; i++) many.Add("7656119800000" + (1000 + i));

        Assert.Equal(Sorting.MaxMembers, Sorting.ParseMembers(Sorting.PackMembers(many)).Count);
    }

    [Fact]
    public void ZonesOfTwoPeopleAreStillZones()
    {
        // Overlap does not care whose they are, and must not: a chest inside two zones has
        // two hosts whether or not the same man drew them.
        var one = Zone(Mine);
        var other = Zone(Friend);
        other.Id = 8;

        Assert.True(Sorting.Overlap(one, other));
    }
}

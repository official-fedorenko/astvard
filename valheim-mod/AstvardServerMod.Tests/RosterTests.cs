using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The one ledger of runes, joined from what the server knows and what the site knows.
/// A mistake here is quiet and expensive: a balance zeroed by the half that never had one,
/// a player missing from the list the admin is paying from, a nickname with a tab in it
/// shifting every field of the row behind it.
/// </summary>
public class RosterTests
{
    private const string Ivan = "76561198000000001";

    private const string Petr = "76561198000000002";

    private const string Stranger = "76561198000000003";

    private static Roster.Row Purse(string id, string character, int balance) =>
        new Roster.Row { Id = id, Character = character, Balance = balance };

    private static Roster.Row Account(string id, string site) =>
        new Roster.Row { Id = id, Site = site };

    [Fact]
    public void TheNameThePersonChoseWins()
    {
        Assert.Equal("Мелиовар", Roster.Display(new Roster.Row
        {
            Id = Ivan, Site = "Мелиовар", Character = "Meliowar-Skald"
        }));

        // No account: the character name is all there is, and it is better than a number.
        Assert.Equal("Meliowar-Skald", Roster.Display(Purse(Ivan, "Meliowar-Skald", 0)));

        // Neither: the id still names somebody the admin can pay.
        Assert.Equal(Ivan, Roster.Display(Account(Ivan, "")));
    }

    [Fact]
    public void BothHalvesMeetOnTheSameId()
    {
        var rows = Roster.Merge(
            new[] { Purse(Ivan, "Meliowar-Skald", 7) },
            new[] { Account(Ivan, "Мелиовар") },
            null);

        var row = Assert.Single(rows);
        Assert.Equal("Мелиовар", row.Site);
        Assert.Equal("Meliowar-Skald", row.Character);
        Assert.Equal(7, row.Balance);
        Assert.True(row.Played);
    }

    [Fact]
    public void TheAccountHalfNeverTouchesTheBalance()
    {
        // The site half carries no balance at all. Reading its zero as a balance would
        // wipe the purse of everyone who happens to have an account - that is, everyone.
        var rows = Roster.Merge(
            new[] { Purse(Ivan, "Meliowar-Skald", 7) },
            new[] { Account(Ivan, "Мелиовар"), Account(Petr, "Пётр") },
            null);

        Assert.Equal(7, rows.Single(r => r.Id == Ivan).Balance);

        var petr = rows.Single(r => r.Id == Petr);
        Assert.Equal(0, petr.Balance);
        Assert.False(petr.Played);   // registered, never came
    }

    [Fact]
    public void WhoeverIsOnRightNowIsInTheList()
    {
        // Somebody on the server that neither half has heard of - a first visit, before any
        // rune is earned - is exactly the person an admin reaches for.
        var rows = Roster.Merge(null, null, new[] { Stranger });

        var row = Assert.Single(rows);
        Assert.Equal(Stranger, row.Id);
        Assert.True(row.Online);
        Assert.False(row.Played);
    }

    [Fact]
    public void NothingWithoutAnIdGetsIn()
    {
        var rows = Roster.Merge(
            new[] { Purse("", "Ничей", 5), Purse(null, "Тоже ничей", 5), Purse(Ivan, "Свой", 1) },
            null, null);

        var row = Assert.Single(rows);
        Assert.Equal(Ivan, row.Id);
    }

    [Fact]
    public void ARepeatedIdIsOneRow()
    {
        var rows = Roster.Merge(
            new[] { Purse(Ivan, "Старое имя", 3), Purse(Ivan, "Новое имя", 5) },
            null, null);

        var row = Assert.Single(rows);
        Assert.Equal("Новое имя", row.Character);
        Assert.Equal(5, row.Balance);
    }

    [Fact]
    public void OnlineFirstThenPlayedThenTheRest()
    {
        var rows = Roster.Merge(
            new[] { Purse(Ivan, "Ярл", 1), Purse(Petr, "Бьорн", 2) },
            new[] { Account(Stranger, "Аки") },
            new[] { Petr });

        Assert.Equal(new[] { Petr, Ivan, Stranger }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void EqualNamesKeepTheirOrder()
    {
        // Two «Викинг»ов sorted by anything unstable would swap places between refreshes,
        // and the admin would press the row that moved.
        var rows = Roster.Merge(
            new[] { Purse(Petr, "Викинг", 0), Purse(Ivan, "Викинг", 0) },
            null, null);

        Assert.Equal(new[] { Ivan, Petr }, rows.Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("мели", true)]      // by the site nickname, any case
    [InlineData("МЕЛИ", true)]
    [InlineData("skald", true)]     // by the character name
    [InlineData("0000001", true)]   // by a piece of the number
    [InlineData("бьорн", false)]
    public void SearchLooksAtEveryNameAndTheNumber(string query, bool found)
    {
        var row = new Roster.Row { Id = Ivan, Site = "Мелиовар", Character = "Meliowar-Skald" };
        Assert.Equal(found, Roster.Matches(row, query));
    }

    [Theory]
    [InlineData("Пётр", "петр")]
    [InlineData("Петр", "пётр")]
    [InlineData("Алёна", "АЛЕНА")]
    public void YoAndYeAreTheSameLetterToSearch(string name, string query)
    {
        // Nobody knows which of the two the person wrote, and a list that answers one
        // spelling and not the other reads as broken rather than as a spelling.
        Assert.True(Roster.Matches(new Roster.Row { Id = Ivan, Site = name }, query));
    }

    [Fact]
    public void SearchReturnsThemInListOrder()
    {
        var rows = Roster.Merge(
            new[] { Purse(Ivan, "Викинг Один", 0), Purse(Petr, "Викинг Два", 0) },
            null, new[] { Petr });

        var found = Roster.Search(rows, "викинг");
        Assert.Equal(new[] { Petr, Ivan }, found.Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData(10, 5, 15)]
    [InlineData(10, -5, 5)]
    [InlineData(3, -10, 0)]            // taking more than there is empties, never owes
    [InlineData(0, 999999999, Roster.MaxGrant)]
    [InlineData(0, -999999999, 0)]
    public void AGrantCannotLeaveABalanceNobodyCanUndo(int balance, int amount, int expected)
    {
        Assert.Equal(expected, Roster.ApplyGrant(balance, amount));
    }

    [Fact]
    public void AHandTypedAmountIsHeldToSomethingSane()
    {
        Assert.Equal(Roster.MaxGrant, Roster.ClampGrant(99999999));
        Assert.Equal(-Roster.MaxGrant, Roster.ClampGrant(-99999999));
        Assert.Equal(7, Roster.ClampGrant(7));
    }

    [Fact]
    public void TheLedgerSurvivesTheTripToThePanel()
    {
        var sent = Roster.Merge(
            new[] { Purse(Ivan, "Meliowar-Skald", 7), Purse(Petr, "Бьорн", 0) },
            new[] { Account(Ivan, "Мелиовар"), Account(Stranger, "Аки") },
            new[] { Petr });

        var got = Roster.Parse(Roster.Pack(sent));

        Assert.Equal(sent.Select(r => r.Id).ToArray(), got.Select(r => r.Id).ToArray());
        Assert.Equal(7, got.Single(r => r.Id == Ivan).Balance);
        Assert.Equal("Мелиовар", got.Single(r => r.Id == Ivan).Site);
        Assert.True(got.Single(r => r.Id == Petr).Online);
        Assert.False(got.Single(r => r.Id == Stranger).Played);
    }

    [Fact]
    public void ANameWithASeparatorInItCannotShiftTheRow()
    {
        // A nickname is whatever the person typed on the site; a tab inside one would move
        // every field behind it one place along, and the row after would read as a balance.
        var packed = Roster.Pack(new[]
        {
            new Roster.Row { Id = Ivan, Site = "Мели\tовар\nБьорн", Balance = 3, Played = true }
        });

        var row = Assert.Single(Roster.Parse(packed));
        Assert.Equal(Ivan, row.Id);
        Assert.Equal(3, row.Balance);
        Assert.Equal("Мели овар Бьорн", row.Site);
    }

    [Fact]
    public void ANewerServerSayingMoreIsStillReadable()
    {
        // Players update when they feel like it, so the panel has to survive a server that
        // knows one more thing about a player than this build asks for.
        var rows = Roster.Parse(Ivan + "\t5\t1\t0\tМелиовар\tMeliowar-Skald\tчто-то новое");

        var row = Assert.Single(rows);
        Assert.Equal(5, row.Balance);
        Assert.Equal("Meliowar-Skald", row.Character);
    }

    [Fact]
    public void AHalfLineIsDroppedRatherThanGuessedAt()
    {
        Assert.Empty(Roster.Parse(Ivan + "\t5"));
        Assert.Empty(Roster.Parse("\t5\t1\t0\tБезымянный"));
        Assert.Empty(Roster.Parse(""));
    }
}

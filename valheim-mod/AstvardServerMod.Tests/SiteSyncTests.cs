using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// What the site says about player builds, as the mod reads it, and what the mod writes
/// into a template file because of it. A wrong split here does not crash anything: it
/// quietly opens a template to everyone, or closes it to the one player it was meant for.
/// </summary>
public class SiteSyncTests
{
    private const string T = "\t";

    [Fact]
    public void AnythingButABuildsStateIsNotAState()
    {
        Assert.False(SiteSync.ParsePull("").Valid);
        Assert.False(SiteSync.ParsePull("<html><body>502 Bad Gateway</body></html>").Valid);
        Assert.False(SiteSync.ParsePull("revision" + T + "abc").Valid);
        Assert.False(SiteSync.ParsePull("revision" + T + "-1").Valid);
    }

    [Fact]
    public void SeedAndUnchangedAreReadAsSuch()
    {
        var seed = SiteSync.ParsePull("revision" + T + "0\nseed" + T + "needed\n");
        Assert.True(seed.Valid);
        Assert.True(seed.SeedNeeded);
        Assert.Empty(seed.Rules);

        var same = SiteSync.ParsePull("revision" + T + "12\r\nunchanged\r\n");
        Assert.True(same.Unchanged);
        Assert.Equal(12, same.Revision);
    }

    [Fact]
    public void RulesComeWithAndWithoutLimits()
    {
        var pulled = SiteSync.ParsePull(string.Join("\n",
            "revision" + T + "7",
            "rule" + T + "floor" + T + "2" + T,
            "rule" + T + "copy" + T + "2" + T + "20",
            "rule" + T + "pause" + T + "0"));

        Assert.Equal(3, pulled.Rules.Count);
        Assert.Null(pulled.Rules[0].Limit);
        Assert.Equal(20, pulled.Rules[1].Limit);
        Assert.Equal("pause", pulled.Rules[2].Key);
        Assert.Equal(0, pulled.Rules[2].Value);
    }

    [Fact]
    public void ALineThatDoesNotReadIsSkippedAndTheRestStillArrives()
    {
        var pulled = SiteSync.ParsePull(string.Join("\n",
            "revision" + T + "3",
            "rule" + T + "Floor" + T + "2",
            "rule" + T + "wall" + T + "two",
            "rule" + T + "fence" + T + "1" + T + "wide",
            "tpl" + T + "Дом" + T + "yes",
            "something" + T + "else",
            "rule" + T + "snap" + T + "1"));

        Assert.True(pulled.Valid);
        Assert.Single(pulled.Rules);
        Assert.Equal("snap", pulled.Rules[0].Key);
        Assert.Empty(pulled.Templates);
    }

    [Fact]
    public void TemplatesCarryTheirPlayersCleanAndInOrder()
    {
        var pulled = SiteSync.ParsePull(string.Join("\n",
            "revision" + T + "4",
            "tpl" + T + "Стартовый дом №1" + T + "0" + T + "76561198000000002, 76561198000000001,x7656,76561198000000002",
            "tpl" + T + "Верстачня" + T + "1" + T));

        Assert.Equal(2, pulled.Templates.Count);
        Assert.Equal("Стартовый дом №1", pulled.Templates[0].Name);
        Assert.False(pulled.Templates[0].ForAll);
        Assert.Equal(new[] { "76561198000000001", "76561198000000002" }, pulled.Templates[0].Players);
        Assert.True(pulled.Templates[1].ForAll);
        Assert.Empty(pulled.Templates[1].Players);
    }

    [Fact]
    public void WhatTheModWritesReadsBackAsTheSame()
    {
        var text = string.Join("\n",
            "revision" + T + "9",
            SiteSync.RuleLine(new SiteSync.RuleState { Key = "bridge", Value = 1, Limit = 30 }),
            SiteSync.RuleLine(new SiteSync.RuleState { Key = "share", Value = 0 }),
            SiteSync.TemplateLine(new SiteSync.TemplateAccess
            {
                Name = "Дом\tс табом",
                ForAll = false,
                Players = new List<string> { "76561198000000005" },
            }));

        var pulled = SiteSync.ParsePull(text);
        Assert.Equal(30, pulled.Rules[0].Limit);
        Assert.Null(pulled.Rules[1].Limit);
        Assert.Equal("Дом с табом", pulled.Templates[0].Name);
        Assert.Equal(new[] { "76561198000000005" }, pulled.Templates[0].Players);
    }

    [Fact]
    public void ANewerChangeToTheSameThingReplacesTheOlderOne()
    {
        var older = SiteSync.RuleLine(new SiteSync.RuleState { Key = "floor", Value = 1 });
        var newer = SiteSync.RuleLine(new SiteSync.RuleState { Key = "floor", Value = 2 });
        var other = SiteSync.RuleLine(new SiteSync.RuleState { Key = "wall", Value = 2 });

        Assert.Equal(SiteSync.IdentityOf(older), SiteSync.IdentityOf(newer));
        Assert.NotEqual(SiteSync.IdentityOf(older), SiteSync.IdentityOf(other));
        Assert.NotEqual(SiteSync.IdentityOf(SiteSync.TemplateAllLine("floor", true)), SiteSync.IdentityOf(older));
    }

    [Theory]
    [InlineData("", "https://astvard.online/api/game/lists", "https://astvard.online/api/game/builds")]
    [InlineData("  ", "http://127.0.0.1:3001/api/game/lists", "http://127.0.0.1:3001/api/game/builds")]
    [InlineData("https://example.org/b", "https://astvard.online/api/game/lists", "https://example.org/b")]
    [InlineData("", "https://astvard.online/somewhere", "")]
    [InlineData("", "", "")]
    public void TheBuildsAddressSitsBesideTheLists(string builds, string lists, string expected)
    {
        Assert.Equal(expected, SiteSync.BuildsUrlFrom(builds, lists));
    }

    [Fact]
    public void ARevisionIsReadOnlyFromARevisionLine()
    {
    }

    private static readonly string[] Template =
    {
        "# astvard shared template",
        "#name Верстачня",
        "#category Верстаки",
        "piece_workbench;0;0;0;0;0;0;1",
    };

    [Fact]
    public void OpeningATemplateToPlayersPutsTheHeadersAfterTheFirstLine()
    {
        var lines = SiteSync.WithAccessHeaders(Template, true,
            new List<string> { "76561198000000002", "76561198000000001" });

        Assert.NotNull(lines);
        Assert.Equal("# astvard shared template", lines[0]);
        Assert.Equal("#players yes", lines[1]);
        Assert.Equal("#allow 76561198000000001,76561198000000002", lines[2]);
        Assert.Equal("#name Верстачня", lines[3]);
        Assert.Equal("piece_workbench;0;0;0;0;0;0;1", lines[lines.Count - 1]);
    }

    [Fact]
    public void AFileThatAlreadySaysSoIsLeftAlone()
    {
        var open = new List<string>(Template);
        open.Insert(1, "#players yes");
        open.Insert(2, "#allow 76561198000000001");

        Assert.Null(SiteSync.WithAccessHeaders(open, true, new List<string> { "76561198000000001" }));
        Assert.Null(SiteSync.WithAccessHeaders(Template, false, new List<string>()));
    }

    [Fact]
    public void ClosingATemplateTakesBothHeadersOutWhateverTheirCase()
    {
        var open = new List<string>(Template);
        open.Insert(1, "#Players yes");
        open.Insert(2, "#ALLOW 76561198000000001");
        open.Add("#allowance is not a header");

        var closed = SiteSync.WithAccessHeaders(open, false, null);

        Assert.NotNull(closed);
        Assert.DoesNotContain(closed, l => l.StartsWith("#players", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(closed, l => l.StartsWith("#allow ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("#allowance is not a header", closed);
        Assert.Equal(Template.Length + 1, closed.Count);
    }

    [Fact]
    public void AnythingButYesIsAClosedTemplate()
    {
        Assert.True(SiteSync.HasAccessHeader("#players no", out var all, out _));
        Assert.False(all);
        Assert.True(SiteSync.HasAccessHeader("  #players yes  ", out all, out _));
        Assert.True(all);
    }
}

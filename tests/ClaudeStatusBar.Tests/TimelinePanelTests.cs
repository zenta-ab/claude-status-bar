using ClaudeStatusBar.Config;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/multi-account.md "Availability timeline": the strip at the top of the full side-by-side container and of the
/// cards panel (never in single mode), its horizon chips, and the horizon setting in accounts.json.
/// </summary>
public class TimelinePanelTests
{
    // views relative to the REAL clock: the form lays the strip out at DateTime.UtcNow
    static readonly TimelineViewFactory F = new(DateTimeOffset.UtcNow);
    static readonly TimeSpan H = TimeSpan.FromHours(1);

    static PanelModel Model(string slot, string label, QuotaView view) =>
        new(slot, view, label, null, Array.Empty<OtherAccountRow>(), false, null, false);

    static PanelModel[] ThreeWithAGap() => new[]
    {
        Model("a", "Personligt", F.WeeklyRunsOut(6 * H, TimeSpan.FromDays(3))),
        Model("b", "Team", F.WeeklyRunsOut(6 * H, 10 * H)),
        Model("c", "Enterprise", F.SpentWeekly(30 * H)),
    };

    static IReadOnlyList<AccountCard> CardsFor(IEnumerable<PanelModel> models) =>
        models.Select(m => AccountCards.Compose(new CardInput(m.Slot!, m.Label!, null, m.View), DateTimeOffset.UtcNow, TimeZoneInfo.Local)).ToList();

    // ---- where the strip is ----

    [Fact]
    public void FullMode_HasTheStripAboveEveryColumn_AsWideAsTheContainer()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(ThreeWithAGap(), "b", demo: false);

        Assert.True(panel.StripHeightForTest > 80f, "one answer line, the chips, the axis, three rows and the coverage row");
        Assert.Equal(3 * 340f, panel.StripWidthForTest);               // exactly the container: the strip never makes it wider
        Assert.NotNull(panel.StripAnswerForTest);
        Assert.Single(panel.FullPanelHeights.Distinct());               // the columns below it are still equal and aligned
    }

    [Fact]
    public void MoreAccounts_MakeTheStripTaller_NotWider()
    {
        using var three = new PanelForm();
        using var five = new PanelForm();
        PanelModel[] models = ThreeWithAGap();

        three.UpdateFull(models, "a", false);
        five.UpdateFull(models.Concat(new[] { Model("d", "Fyra", F.Open()), Model("e", "Fem", F.Open()) }).ToArray(), "a", false);

        Assert.True(five.StripHeightForTest > three.StripHeightForTest + 20f, "two more rows");
        Assert.Equal(5 * 340f, five.StripWidthForTest);                 // as wide as its five columns, and no wider
    }

    [Fact]
    public void FullMode_StripAnswer_IsTheModelsAnswer_ForTheSameAccounts()
    {
        using var panel = new PanelForm();
        PanelModel[] models = ThreeWithAGap();

        panel.UpdateFull(models, "a", false);

        var expected = AvailabilityTimeline.Build(models.Select(m => (m.Label!, m.View)).ToList(), DateTimeOffset.UtcNow, TimelineHorizon.Hours24);
        Assert.Equal(TimelineText.Answer(expected, TimelineHorizon.Hours24, TimeZoneInfo.Local), panel.StripAnswerForTest);
        Assert.StartsWith("Lucka", panel.StripAnswerForTest);
        Assert.Equal(3, panel.StripTimelineForTest!.Accounts.Count);
        Assert.Equal(new[] { "Personligt", "Team", "Enterprise" }, panel.StripTimelineForTest!.Accounts.Select(a => a.Label).ToArray());   // the columns' order
    }

    [Fact]
    public void CardsMode_HasTheStrip_WhenGivenTheAccounts_AndTheCardsStartBelowIt()
    {
        using var with = new PanelForm();
        using var without = new PanelForm();
        PanelModel[] models = ThreeWithAGap();
        var timeline = models.Select(m => new TimelineAccount(m.Label!, m.View)).ToList();

        with.UpdateCombined(CardsFor(models), "a", false, timeline);
        without.UpdateCombined(CardsFor(models), "a", false);

        Assert.True(with.StripHeightForTest > 80f);
        Assert.Equal(0f, without.StripHeightForTest);
        Assert.True(with.CardRectsForTest[0].Y >= without.CardRectsForTest[0].Y + with.StripHeightForTest, "the cards sit below the strip");
        Assert.Equal(340f, with.StripWidthForTest);
    }

    [Fact]
    public void SingleMode_HasNoStrip()
    {
        using var panel = new PanelForm();

        panel.UpdateView(F.Open(), false, "A", Array.Empty<OtherAccountRow>(), false, null, "Max", false, "a");

        Assert.Equal(0f, panel.StripHeightForTest);
        Assert.Null(panel.StripAnswerForTest);
    }

    // ---- the horizon chips ----

    [Fact]
    public void Horizon_DefaultsTo24h_AndClickingAChipChangesItAndSaysSo()
    {
        using var panel = new PanelForm();
        var raised = new List<TimelineHorizon>();
        panel.TimelineHorizonChanged += raised.Add;
        panel.UpdateFull(new[] { Model("a", "A", F.Open()), Model("b", "B", F.Open()) }, "a", false);
        Assert.Equal(TimelineHorizon.Hours24, panel.Horizon);
        Assert.Equal("Ingen lucka de närmaste 24 h", panel.StripAnswerForTest);

        panel.ClickHorizonChipForTest(TimelineHorizon.Days3);

        Assert.Equal(TimelineHorizon.Days3, panel.Horizon);
        Assert.Equal(new[] { TimelineHorizon.Days3 }, raised);
        Assert.Equal("Ingen lucka de närmaste 3 d", panel.StripAnswerForTest);

        panel.ClickHorizonChipForTest(TimelineHorizon.Days3);            // the same chip again: nothing changes, nothing is raised
        Assert.Single(raised);

        panel.ClickHorizonChipForTest(TimelineHorizon.Days7);
        Assert.Equal(new[] { TimelineHorizon.Days3, TimelineHorizon.Days7 }, raised);
    }

    [Fact]
    public void RestoringTheSavedHorizon_DoesNotRaiseTheChangedEvent()
    {
        using var panel = new PanelForm();
        int raised = 0;
        panel.TimelineHorizonChanged += _ => raised++;

        panel.SetTimelineHorizon(TimelineHorizon.Days7);

        Assert.Equal(TimelineHorizon.Days7, panel.Horizon);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ALongerHorizon_ChangesTheModelsReach()
    {
        using var panel = new PanelForm();
        panel.UpdateFull(new[] { Model("a", "A", F.SpentWeekly(2 * H)), Model("b", "B", F.SpentWeekly(TimeSpan.FromDays(2))) }, "a", false);
        AvailabilityTimeline at24 = panel.StripTimelineForTest!;
        TimeSpan reach24 = at24.HorizonEnd - at24.Now;

        panel.SetTimelineHorizon(TimelineHorizon.Days7);
        AvailabilityTimeline at7d = panel.StripTimelineForTest!;
        TimeSpan reach7d = at7d.HorizonEnd - at7d.Now;

        Assert.Equal(TimeSpan.FromHours(24), reach24);
        Assert.Equal(TimeSpan.FromDays(7), reach7d);
    }

    [Fact]
    public void DemoHover_OnASegment_DoesNotThrow_AndClearsAgain()
    {
        using var panel = new PanelForm();
        panel.UpdateFull(ThreeWithAGap(), "b", false);

        panel.SetStripHoverForDemo((1, 1, 0.5f));
        panel.SetStripHoverForDemo(null);
        panel.SetStripHoverForDemo((9, 9, 0.5f));          // out of range: ignored
    }
}

/// <summary>The optional "timelineHorizon" field of accounts.json: absent until the user clicks a chip, "24h" | "3d" | "7d", anything else quarantines the file.</summary>
public class TimelineHorizonSettingTests
{
    static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "accounts.json");
    }

    static void Cleanup(string path) => Directory.Delete(Path.GetDirectoryName(path)!, true);

    [Theory]
    [InlineData("24h", TimelineHorizon.Hours24)]
    [InlineData("3d", TimelineHorizon.Days3)]
    [InlineData("7d", TimelineHorizon.Days7)]
    [InlineData("3D", TimelineHorizon.Days3)]
    public void Parses(string text, TimelineHorizon expected)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, $"{{ \"version\": 2, \"timelineHorizon\": \"{text}\", \"accounts\": [] }}");
            AccountsConfigLoad load = AccountsConfig.Load(path);
            Assert.Equal(AccountsConfigStatus.Loaded, load.Status);
            Assert.Equal(expected, load.Config.TimelineHorizonChoice);
            Assert.Equal(expected, load.Config.EffectiveTimelineHorizon);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Absent_IsTwentyFourHours_AndIsNotWrittenBack()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"version\": 2, \"accounts\": [] }");
            AccountsConfig config = AccountsConfig.Load(path).Config;
            Assert.Null(config.TimelineHorizonChoice);
            Assert.Equal(TimelineHorizon.Hours24, config.EffectiveTimelineHorizon);

            AccountsConfig.Save(config, path);

            Assert.DoesNotContain("timelineHorizon", File.ReadAllText(path));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(TimelineHorizon.Hours24, "24h")]
    [InlineData(TimelineHorizon.Days3, "3d")]
    [InlineData(TimelineHorizon.Days7, "7d")]
    public void RoundTrips(TimelineHorizon horizon, string written)
    {
        string path = TempPath();
        try
        {
            var config = new AccountsConfig { Version = 2, TimelineHorizonChoice = horizon };
            AccountsConfig.Save(config, path);

            Assert.Contains($"\"timelineHorizon\": \"{written}\"", File.ReadAllText(path));
            Assert.Equal(horizon, AccountsConfig.Load(path).Config.TimelineHorizonChoice);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("\"5d\"")]
    [InlineData("24")]
    [InlineData("true")]
    public void UnknownValues_QuarantineTheFile_LikeAnyOtherInvalidOne(string value)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, $"{{ \"version\": 2, \"timelineHorizon\": {value}, \"accounts\": [] }}");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Quarantined, load.Status);
            Assert.False(File.Exists(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TheHorizonAndThePanelMode_LiveTogether_WithoutDisturbingEachOther()
    {
        string path = TempPath();
        try
        {
            AccountsConfig.Save(new AccountsConfig { Version = 2, PanelMode = PanelDisplayMode.Full, TimelineHorizonChoice = TimelineHorizon.Days3 }, path);

            AccountsConfig loaded = AccountsConfig.Load(path).Config;

            Assert.Equal(PanelDisplayMode.Full, loaded.PanelMode);
            Assert.Equal(TimelineHorizon.Days3, loaded.TimelineHorizonChoice);
        }
        finally { Cleanup(path); }
    }
}

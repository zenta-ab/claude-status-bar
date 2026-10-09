using System.Drawing;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/multi-account.md "Panels": the three panel settings (parsing, the old "all" alias, persistence),
/// and the full side-by-side mode of PanelForm -- panels in the order given, the marked one, the omitted
/// "other accounts" section, the one solid container (equal columns, aligned rows, dividers, the marking strip, dismissal), the mode switches, and that single and cards are unchanged.
/// </summary>
public class FullPanelsTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    static QuotaView SafeView() => new(
        new WindowView(WindowKind.Session, 300, 18, Now.AddHours(3), QuotaState.Safe, 0.01, 1, 28, null, TimeSpan.Zero, null),
        new WindowView(WindowKind.Weekly, 10080, 12, Now.AddDays(5), QuotaState.Safe, 0.001, 1, 20, null, TimeSpan.Zero, null),
        Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

    static readonly OtherAccountRow[] Rows = { new(1, "Andra", "räcker till reset", PanelColorRole.Safe) };

    static PanelModel Model(string slot, QuotaView? view = null) =>
        new(slot, view ?? SafeView(), "A", "Max", Array.Empty<OtherAccountRow>(), false, null, false);

    // ---- the setting ----

    static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-panelmodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "accounts.json");
    }

    [Theory]
    [InlineData("single", PanelDisplayMode.Single)]
    [InlineData("full", PanelDisplayMode.Full)]
    [InlineData("cards", PanelDisplayMode.Cards)]
    [InlineData("all", PanelDisplayMode.Cards)]   // the earlier value
    [InlineData("FULL", PanelDisplayMode.Full)]
    public void PanelMode_Parses(string text, PanelDisplayMode expected)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, $$"""{"version":2,"maxIcons":3,"panelMode":"{{text}}","accounts":[]}""");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Loaded, load.Status);
            Assert.Equal(expected, load.Config.EffectivePanelMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void PanelMode_TheOldAllValue_IsWrittenBackAsCards_OnTheNextSave()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """{"version":2,"maxIcons":3,"panelMode":"all","accounts":[]}""");

            AccountsConfig.Save(AccountsConfig.Load(path).Config, path);

            string saved = File.ReadAllText(path);
            Assert.Contains("\"panelMode\": \"cards\"", saved);
            Assert.DoesNotContain("\"all\"", saved);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    [InlineData("1")]
    public void PanelMode_UnknownValues_AreStillQuarantined(string text)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, $$"""{"version":2,"maxIcons":3,"panelMode":"{{text}}","accounts":[]}""");

            Assert.Equal(AccountsConfigStatus.Quarantined, AccountsConfig.Load(path).Status);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void PanelMode_Full_RoundTrips_AndSingleIsNeverWritten()
    {
        string path = TempPath();
        try
        {
            AccountsConfig.Save(new AccountsConfig { Version = 2, PanelMode = PanelDisplayMode.Full }, path);
            Assert.Contains("\"panelMode\": \"full\"", File.ReadAllText(path));
            Assert.Equal(PanelDisplayMode.Full, AccountsConfig.Load(path).Config.EffectivePanelMode);

            AccountsConfig.Save(new AccountsConfig { Version = 2 }, path);
            Assert.DoesNotContain("panelMode", File.ReadAllText(path));
            Assert.Equal(PanelDisplayMode.Single, AccountsConfig.Load(path).Config.EffectivePanelMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    // ---- full mode in the form ----

    [Fact]
    public void Full_ShowsOnePanelPerAccount_InTheOrderGiven_WithTheClickedOneMarked()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(new[] { Model("c"), Model("a"), Model("b") }, markedSlot: "a", demo: false);

        Assert.True(panel.IsFull);
        Assert.False(panel.IsCombined);
        Assert.Equal(new[] { "c", "a", "b" }, panel.FullPanelSlots);
        Assert.Equal(1, panel.MarkedPanelIndex);
        Assert.Equal(3, panel.FullPanelHeights.Count);
    }

    [Fact]
    public void Full_AMarkedSlotThatIsNotShown_MarksNothing()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(new[] { Model("a") }, markedSlot: "zzz", demo: false);

        Assert.Equal(-1, panel.MarkedPanelIndex);
    }

    [Fact]
    public void Full_OmitsTheOtherAccountsSection_AndTheBackLink()
    {
        using var panel = new PanelForm();
        var withRows = new PanelModel("a", SafeView(), "A", "Max", Rows, false, null, BackLink: true);

        panel.UpdateFull(new[] { withRows }, "a", false);
        float stripped = panel.FullPanelHeights[0];
        panel.UpdateFull(new[] { Model("a") }, "a", false);

        Assert.Equal(panel.FullPanelHeights[0], stripped); // rows and back link were removed by the form itself
    }

    [Fact]
    public void Full_APanelIsExactlyTheSingleRendering_SameHeightAsTheSinglePanelWithoutRows()
    {
        using var single = new PanelForm();
        using var full = new PanelForm();
        QuotaView view = SafeView();

        single.UpdateView(view, false, "A", null, false, null, "Max", false, "a");
        full.UpdateFull(new[] { Model("a", view) }, "a", false);

        Assert.Equal(single.SingleHeightForTest, full.FullPanelHeights[0]);
    }

    const string LongLabel = "Ett mycket langt kontonamn som maste brytas over flera rader i rubriken";

    /// <summary>Three columns that differ in every block: a plain one, one needing a login (button), one with a wrapping title and subtitle.</summary>
    static PanelModel[] UnevenColumns() => new[]
    {
        Model("a"),
        Model("b", QuotaView.Initial with { NeedsLogin = true }),
        new PanelModel("c", SafeView(), LongLabel, "Max - " + LongLabel, Array.Empty<OtherAccountRow>(), false, null, false),
    };

    [Fact]
    public void Full_EveryColumnIsAsTallAsTheTallest()
    {
        using var panel = new PanelForm();
        PanelModel[] columns = UnevenColumns();

        panel.UpdateFull(columns, "a", false);

        IReadOnlyList<float> heights = panel.FullPanelHeights;
        Assert.Equal(3, heights.Count);
        Assert.Single(heights.Distinct());
        Assert.True(heights[0] > 200);
        // ... at least the tallest column's own natural height (they are tall in different blocks, so usually more)
        var naturals = new List<float>();
        foreach (PanelModel m in columns)
        {
            using var one = new PanelForm();
            one.UpdateFull(new[] { m }, null, false);
            naturals.Add(one.FullPanelHeights[0]);
        }
        Assert.True(naturals.Distinct().Count() > 1, "the fixture must have columns of different natural heights");
        Assert.True(heights[0] >= naturals.Max());
    }

    [Fact]
    public void Full_TheRowsLineUpAcrossColumns_LikeATable()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(UnevenColumns(), "a", false);

        IReadOnlyList<PanelForm.FullRowYs> rows = panel.FullRows;
        Assert.Equal(3, rows.Count);
        Assert.Single(rows.Select(r => r.StatusBoxY).Distinct());
        Assert.Single(rows.Select(r => r.StatusBoxHeight).Distinct());
        Assert.Single(rows.Select(r => r.SessionY).Distinct());
        Assert.Single(rows.Select(r => r.WeeklyY).Distinct());
        Assert.Single(rows.Select(r => r.RuleY).Distinct());
        Assert.Single(rows.Select(r => r.FooterY).Distinct());
        Assert.Single(rows.Select(r => r.Height).Distinct());
    }

    [Fact]
    public void Full_AColumnTallerInOneBlockOnlyPushesTheOthersDown_NeverShrinksAnyone()
    {
        using var alone = new PanelForm();
        using var together = new PanelForm();
        PanelModel plain = Model("a");

        alone.UpdateFull(new[] { plain }, "a", false);
        together.UpdateFull(UnevenColumns(), "a", false);

        Assert.True(together.FullRows[0].SessionY > alone.FullRows[0].SessionY, "the plain column made room for the login button");
        Assert.True(together.FullRows[0].FooterY > alone.FullRows[0].FooterY);
    }

    [Fact]
    public void Full_ColumnsTouch_DividersSitOnTheJoints_AndAreOneColumnApart()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(new[] { Model("a"), Model("b"), Model("c"), Model("d") }, "a", false);

        IReadOnlyList<float> dividers = panel.FullDividerXs;
        Assert.Equal(3, dividers.Count); // n columns, n-1 dividers
        float width = dividers[0];       // the first column ends where the first divider is: no gap, no margin
        Assert.True(width > 300);
        Assert.Equal(new[] { width, 2 * width, 3 * width }, dividers);
    }

    [Fact]
    public void Full_OneColumn_HasNoDivider()
    {
        using var panel = new PanelForm();

        panel.UpdateFull(new[] { Model("a") }, "a", false);

        Assert.Empty(panel.FullDividerXs);
    }

    [Fact]
    public void Full_TheClickedColumnHasAnAccentStripAlongItsTop_NoOtherDoes()
    {
        using var panel = new PanelForm();
        panel.UpdateFull(new[] { Model("a"), Model("b"), Model("c") }, "b", false);

        RectangleF strip = panel.FullMarkStrip!.Value;

        Assert.Equal(panel.FullDividerXs[0], strip.X);  // the second column
        Assert.Equal(0f, strip.Y);                      // along the top edge
        Assert.InRange(strip.Height, 2f, 3f);
        Assert.Equal(panel.FullDividerXs[0], strip.Width);

        panel.UpdateFull(new[] { Model("a"), Model("b"), Model("c") }, "zzz", false);
        Assert.Null(panel.FullMarkStrip);
    }

    [Fact]
    public void Full_TheWholeContainerIsInsideForDismissal_AnythingOutsideIsNot()
    {
        using var panel = new PanelForm();
        panel.UpdateFull(UnevenColumns(), "a", false);
        panel.Bounds = new Rectangle(100, 100, 1020, 480);

        // corners, the joints between columns (divider and the strip above/below it) and the middle: all inside
        foreach (Point p in new[] { new Point(100, 100), new Point(1119, 579), new Point(100, 579), new Point(1119, 100),
                                    new Point(440, 110), new Point(440, 575), new Point(440, 340), new Point(780, 340), new Point(500, 340) })
            Assert.True(panel.IsInsidePanelsForTest(p), $"{p} is inside the container");

        foreach (Point p in new[] { new Point(99, 300), new Point(1120, 300), new Point(500, 99), new Point(500, 580) })
            Assert.False(panel.IsInsidePanelsForTest(p), $"{p} is outside");
    }

    [Fact]
    public void Modes_SwitchBetweenSingleFullAndCards()
    {
        using var panel = new PanelForm();
        panel.UpdateFull(new[] { Model("a"), Model("b") }, "a", false);
        Assert.True(panel.IsFull);

        panel.UpdateView(SafeView(), false, "A", Rows, false, null, "Max", true, "a");
        Assert.False(panel.IsFull);
        Assert.False(panel.IsCombined);

        panel.UpdateCombined(new[] { AccountCards.Compose(new CardInput("a", "A", null, SafeView()), Now, TimeZoneInfo.Utc) }, "a", false);
        Assert.True(panel.IsCombined);
        Assert.False(panel.IsFull);

        panel.UpdateFull(new[] { Model("a") }, "a", false);
        Assert.True(panel.IsFull);
        Assert.False(panel.IsCombined);
    }

    [Fact]
    public void Single_StillShowsTheOtherAccountsRows_AndTheBackLinkOnlyWhenAsked()
    {
        using var plain = new PanelForm();
        using var rows = new PanelForm();
        using var back = new PanelForm();

        plain.UpdateView(SafeView(), false, "A", null, false, null, "Max", false, "a");
        rows.UpdateView(SafeView(), false, "A", Rows, false, null, "Max", false, "a");
        back.UpdateView(SafeView(), false, "A", null, false, null, "Max", true, "a");

        Assert.True(rows.SingleHeightForTest > plain.SingleHeightForTest, "the other-accounts section is still part of the single panel");
        Assert.True(back.SingleHeightForTest > plain.SingleHeightForTest, "the back link row is still part of the single panel");
    }

    [Fact]
    public void Cards_AreUnchanged_OneCardPerAccount_MarkedByTheClickedSlot()
    {
        using var panel = new PanelForm();
        var cards = new[] { "a", "b", "c" }.Select(s => AccountCards.Compose(new CardInput(s, s, null, SafeView()), Now, TimeZoneInfo.Utc)).ToList();

        panel.UpdateCombined(cards, "b", false);

        Assert.Equal(1, panel.MarkedCardIndex);
        Assert.Equal(3, panel.CardRectsForTest.Count);
    }

    // ---- placement ----

    [Fact]
    public void Anchor_ARowOfPanels_HasItsRightEdgeWhereOnePanelsRightEdgeWouldBe()
    {
        var screen = new Rectangle(0, 0, 2560, 1440);
        var work = new Rectangle(0, 0, 2560, 1392); // taskbar at the bottom
        var icon = new Rectangle(2000, 1392, 24, 48);
        const int single = 340;

        Point one = PanelAnchor.Compute(icon, screen, work, new Size(single, 600));
        Point row = PanelAnchor.Compute(icon, screen, work, new Size(3 * single + 16, 600), rightAlignSinglePhysicalWidth: single);

        Assert.Equal(one.X + single, row.X + 3 * single + 16);
        Assert.True(row.X >= work.Left + 8);
    }

    [Fact]
    public void Anchor_ARowThatWouldLeaveTheScreen_IsClampedInside()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var work = new Rectangle(0, 0, 1920, 1032);
        var icon = new Rectangle(300, 1032, 24, 48);

        Point row = PanelAnchor.Compute(icon, screen, work, new Size(1000, 600), rightAlignSinglePhysicalWidth: 340);

        Assert.True(row.X >= work.Left + 8);
        Assert.True(row.X + 1000 <= work.Right - 8);
    }

    [Fact]
    public void Anchor_WithoutRightAlignment_IsTheOldCentredPlacement()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var work = new Rectangle(0, 0, 1920, 1032);
        var icon = new Rectangle(1000, 1032, 24, 48);

        Point p = PanelAnchor.Compute(icon, screen, work, new Size(340, 600));

        Assert.Equal(1012 - 170, p.X);
    }
}

using System.Drawing;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/multi-account.md "All accounts in one panel": the cards (wording shared with the status box,
/// day-aware times, a note only when not Live), their order (icons left to right, then the accounts
/// without an icon in config order), the marked card, hidden accounts included, and the setting's
/// persistence. Placeholder names only.
/// </summary>
public class AccountCardsTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    static DateTimeOffset Local(int y, int mo, int d, int h, int mi) =>
        new(new DateTime(y, mo, d, h, mi, 0), Tz.GetUtcOffset(new DateTime(y, mo, d, h, mi, 0)));
    static readonly DateTimeOffset Now = Local(2026, 10, 7, 14, 0);

    static WindowView Win(WindowKind kind, double minutes, double pct, DateTimeOffset? resets, QuotaState state, string? reason = null) =>
        new(kind, minutes, pct, resets, state, 0.01, 1, pct + 10, null, TimeSpan.Zero, reason);

    static QuotaView Live(WindowView session, WindowView weekly, DateTimeOffset? blocked = null) =>
        new(session, weekly, Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null,
            (QuotaState)Math.Max((int)session.State, (int)weekly.State), blocked);

    static QuotaView SafeView() => Live(
        Win(WindowKind.Session, 300, 18, Local(2026, 10, 7, 17, 0), QuotaState.Safe),
        Win(WindowKind.Weekly, 10080, 12, Local(2026, 10, 13, 15, 0), QuotaState.Safe));

    // ---- composition ----

    [Fact]
    public void Card_CarriesTitleSubtitleVerdict_AndTwoBarsWithDayAwareResets()
    {
        AccountCard card = AccountCards.Compose(new CardInput("a1", "Privat", "Max · personlig organisation", SafeView()), Now, Tz);

        Assert.Equal("Privat", card.Title);
        Assert.Equal("Max · personlig organisation", card.Subtitle);
        Assert.Equal("✓ Kvoten räcker till reset", card.Verdict);
        Assert.Equal(PanelColorRole.Safe, card.VerdictRole);
        Assert.Equal("Session", card.Session.Caption);
        Assert.Equal("18 % · reset 17:00", card.Session.Text);
        Assert.Equal("Vecka", card.Week.Caption);
        Assert.Equal("12 % · reset tis 15:00", card.Week.Text); // not today: its day
        Assert.Equal(0.18, card.Session.Fraction, 3);
        Assert.Null(card.Note); // Live: no note
    }

    [Fact]
    public void Card_VerdictIsTheStatusBoxsFirstLine_SameWording()
    {
        QuotaView view = Live(
            Win(WindowKind.Session, 300, 100, Local(2026, 10, 7, 17, 19), QuotaState.Spent),
            Win(WindowKind.Weekly, 10080, 40, Local(2026, 10, 13, 15, 0), QuotaState.Safe), Local(2026, 10, 7, 17, 19));

        AccountCard card = AccountCards.Compose(new CardInput("a1", "Jobb", null, view), Now, Tz);

        Assert.Equal(PanelText.Compose(view, Now, Tz).StatusBox.Line1, card.Verdict);
        Assert.Equal("100 % · öppnar 17:19", card.Session.Text);
        Assert.Equal(1.0, card.Session.Fraction);
    }

    [Fact]
    public void Card_SpentWeekReopeningTomorrow_SaysIMorgon()
    {
        QuotaView view = Live(
            Win(WindowKind.Session, 300, 5, Local(2026, 10, 7, 18, 0), QuotaState.Safe),
            Win(WindowKind.Weekly, 10080, 100, Local(2026, 10, 8, 7, 0), QuotaState.Spent), Local(2026, 10, 8, 7, 0));

        Assert.Equal("100 % · öppnar i morgon 07:00", AccountCards.Compose(new CardInput("a1", "Jobb", null, view), Now, Tz).Week.Text);
    }

    [Fact]
    public void Card_NoteOnlyWhenNotLive()
    {
        QuotaView stale = SafeView() with { Freshness = Freshness.Stale, LastChangedAt = Now.AddMinutes(-23) };
        QuotaView unknown = QuotaView.Initial with { LastSuccessAt = Now.AddHours(-3) };
        QuotaView loading = QuotaView.Initial with { Loading = true };
        QuotaView needsLogin = QuotaView.Initial with { NeedsLogin = true };

        Assert.Null(AccountCards.Compose(new CardInput("a", "A", null, SafeView()), Now, Tz).Note);
        Assert.StartsWith("Inaktuell", AccountCards.Compose(new CardInput("a", "A", null, stale), Now, Tz).Note);
        Assert.Contains("23 min", AccountCards.Compose(new CardInput("a", "A", null, stale), Now, Tz).Note);
        Assert.StartsWith("Senast avläst kl 11:00", AccountCards.Compose(new CardInput("a", "A", null, unknown), Now, Tz).Note);
        Assert.Equal("Hämtar…", AccountCards.Compose(new CardInput("a", "A", null, loading), Now, Tz).Note);
        Assert.Equal("Inte inloggad", AccountCards.Compose(new CardInput("a", "A", null, needsLogin), Now, Tz).Note);
    }

    [Fact]
    public void Card_OffersReloginOnlyWhenNeedsLogin()
    {
        Assert.True(AccountCards.Compose(new CardInput("a", "A", null, QuotaView.Initial with { NeedsLogin = true }), Now, Tz).OfferRelogin);
        Assert.False(AccountCards.Compose(new CardInput("a", "A", null, QuotaView.Initial with { Loading = true }), Now, Tz).OfferRelogin);
        Assert.False(AccountCards.Compose(new CardInput("a", "A", null, SafeView()), Now, Tz).OfferRelogin);
    }

    [Fact]
    public void Card_LoadingStatusBoxWording_AndNoEmailAnywhere()
    {
        AccountCard card = AccountCards.Compose(new CardInput("a", "A", "Team · Acme AB", QuotaView.Initial with { Loading = true }), Now, Tz);

        Assert.Equal("Hämtar kvoten…", card.Verdict);
        Assert.DoesNotContain("@", string.Join(" ", card.Title, card.Subtitle, card.Verdict, card.Session.Text, card.Week.Text, card.Note));
    }

    // ---- order ----

    static (string, Rectangle?) Icon(string slot, int left) => (slot, new Rectangle(left, 1000, 24, 40));

    [Fact]
    public void Order_FollowsTheIconsLeftToRight_NotConfigOrder()
    {
        var icons = new[] { Icon("c", 100), Icon("a", 300), Icon("b", 200) };

        IReadOnlyList<string> order = AccountCards.Order(icons, new[] { "a", "b", "c" });

        Assert.Equal(new[] { "c", "b", "a" }, order);
    }

    [Fact]
    public void Order_AccountsWithoutAnIcon_ComeLast_InConfigOrder_AndAreIncluded()
    {
        // maxIcons 2 of 5, or binding mode: only "b" and "d" have icons.
        var icons = new[] { Icon("d", 50), Icon("b", 90) };

        IReadOnlyList<string> order = AccountCards.Order(icons, new[] { "a", "b", "c", "d", "e" });

        Assert.Equal(new[] { "d", "b", "a", "c", "e" }, order);
        Assert.Equal(5, order.Count);
    }

    [Fact]
    public void Order_NoIconAtAll_IsConfigOrder()
    {
        Assert.Equal(new[] { "a", "b", "c" }, AccountCards.Order(Array.Empty<(string, Rectangle?)>(), new[] { "a", "b", "c" }));
    }

    [Fact]
    public void Order_WhenAnIconRectangleCannotBeRead_TheIconGroupFallsBackToConfigOrder()
    {
        var icons = new (string, Rectangle?)[] { ("c", new Rectangle(100, 0, 24, 24)), ("a", null), ("b", new Rectangle(50, 0, 24, 24)) };

        IReadOnlyList<string> order = AccountCards.Order(icons, new[] { "a", "b", "c", "d" });

        Assert.Equal(new[] { "a", "b", "c", "d" }, order); // not a mix of two orderings
    }

    [Fact]
    public void Order_IgnoresIconsOfAccountsThatAreNotShown_AndNeverDuplicates()
    {
        var icons = new[] { Icon("gone", 10), Icon("b", 20) };

        IReadOnlyList<string> order = AccountCards.Order(icons, new[] { "a", "b" });

        Assert.Equal(new[] { "b", "a" }, order);
    }

    // ---- the form: marked card, every account has a card ----

    [Fact]
    public void Panel_MarksTheCardOfTheClickedIcon_AndLaysOutACardPerAccount()
    {
        using var panel = new PanelForm();
        var cards = new[] { "a", "b", "c", "d" }
            .Select(s => AccountCards.Compose(new CardInput(s, s.ToUpperInvariant(), null, SafeView()), Now, Tz)).ToList();

        panel.UpdateCombined(cards, markedSlot: "c", demo: false);

        Assert.True(panel.IsCombined);
        Assert.Equal(2, panel.MarkedCardIndex);
        Assert.Equal(4, panel.CardRectsForTest.Count);
        Assert.True(panel.CardRectsForTest.Zip(panel.CardRectsForTest.Skip(1), (p, n) => n.Top > p.Bottom).All(x => x), "cards overlap");
    }

    [Fact]
    public void Panel_NoMarkedCard_WhenTheMarkedSlotIsNotShown()
    {
        using var panel = new PanelForm();
        var cards = new[] { AccountCards.Compose(new CardInput("a", "A", null, SafeView()), Now, Tz) };

        panel.UpdateCombined(cards, markedSlot: "zzz", demo: false);

        Assert.Equal(-1, panel.MarkedCardIndex);
    }

    [Fact]
    public void Panel_UpdateView_LeavesCombinedMode()
    {
        using var panel = new PanelForm();
        panel.UpdateCombined(new[] { AccountCards.Compose(new CardInput("a", "A", null, SafeView()), Now, Tz) }, "a", demo: false);

        panel.UpdateView(SafeView(), demo: false, showBackLink: true);

        Assert.False(panel.IsCombined);
    }

    // ---- the setting ----

    static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-panelmode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "accounts.json");
    }

    [Fact]
    public void PanelMode_DefaultsToSingle_AndIsNotWrittenUnlessSet()
    {
        string path = TempPath();
        try
        {
            var config = new AccountsConfig { Version = 2, Accounts = new List<AccountEntry> { new() { Slot = "a1b2c3d4" } } };
            AccountsConfig.Save(config, path);

            Assert.DoesNotContain("panelMode", File.ReadAllText(path)); // an existing user's file is unchanged
            Assert.Equal(PanelDisplayMode.Single, AccountsConfig.Load(path).Config.EffectivePanelMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void PanelMode_All_PersistsAndRoundTrips()
    {
        string path = TempPath();
        try
        {
            var config = new AccountsConfig { Version = 2, PanelMode = PanelDisplayMode.All };
            AccountsConfig.Save(config, path);

            Assert.Contains("\"panelMode\": \"all\"", File.ReadAllText(path));
            Assert.Equal(PanelDisplayMode.All, AccountsConfig.Load(path).Config.EffectivePanelMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void PanelMode_TurnedBackOff_LeavesNoField_AndUnknownValuesAreRejectedNotGuessed()
    {
        string path = TempPath();
        try
        {
            var config = new AccountsConfig { Version = 2, PanelMode = PanelDisplayMode.All };
            config.PanelMode = null;
            AccountsConfig.Save(config, path);
            Assert.DoesNotContain("panelMode", File.ReadAllText(path));

            File.WriteAllText(path, """{"version":2,"maxIcons":3,"panelMode":"sideways","accounts":[]}""");
            Assert.Equal(AccountsConfigStatus.Quarantined, AccountsConfig.Load(path).Status);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}

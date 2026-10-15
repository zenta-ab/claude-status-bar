using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Two things the full panel showed wrongly for an IDLE account (docs/forecast-and-states.md "An idle account is not
/// a stale one"): a session window that has closed read "återställs nu · igår kl 23:20" with "100 % av 5 h har gått",
/// and the header said "Uppdaterad för 10 h 39 min sedan", which looks like an error when nothing is used and so
/// nothing changes.
/// </summary>
public class IdleDisplayTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    static string Raw(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    static WindowView ClosedSession() => new(WindowKind.Session, QuotaWindows.SessionMinutes, UsedPct: null, ResetsAt: null, QuotaState.Measuring,
        RatePctPerMin: null, PaceMultiple: null, ProjectedPctAtReset: null, DepletesAt: null, Shortfall: TimeSpan.Zero, MeasuringReason: "Inget förbrukat ännu");

    static QuotaView IdleView(TimeSpan changedAgo, Freshness freshness = Freshness.Live) => new(
        ClosedSession(), TimelineViews.Safe(WindowKind.Weekly, TimeSpan.FromDays(4)), freshness,
        Now - changedAgo, Now.AddSeconds(-2), TimeSpan.FromMinutes(10), null, QuotaState.Safe, null);

    // ---- bug a: a closed session ----

    [Fact]
    public void Model_ASessionThatClosedAfterBeingOpen_HasNoDeadlineAndNoPercentage()
    {
        var model = new QuotaModel();
        var t0 = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
        DateTimeOffset sessionEnd = t0.AddHours(5), weeklyReset = t0.AddDays(3);

        // an open session, polled for a while ...
        long mono = 0;
        DateTimeOffset now = t0;
        for (int i = 0; i < 6; i++)
        {
            now = t0.AddMinutes(i * 5);
            mono = i * 300_000L;
            model.Ingest(new UsageSnapshot(10 + i * 3, Raw(sessionEnd), 20 + i, Raw(weeklyReset), now, "max"), now, mono);
        }
        Assert.Equal(sessionEnd, model.Evaluate(now, mono).Session.ResetsAt);

        // ... then the server reports it closed (nothing used since): no deadline, nothing to count down to
        now = sessionEnd.AddMinutes(20);
        mono = (long)(now - t0).TotalMilliseconds;
        model.Ingest(new UsageSnapshot(0.0, null, 25.0, Raw(weeklyReset), now, "max", SessionIsActive: false, WeeklyIsActive: true), now, mono);

        QuotaView view = model.Evaluate(now, mono);
        Assert.Null(view.Session.ResetsAt);                       // the old deadline is history, not an overdue reset
        Assert.Null(view.Session.UsedPct);
        Assert.Equal(QuotaState.Measuring, view.Session.State);
        Assert.Equal("Inget förbrukat ännu", view.Session.MeasuringReason);
        Assert.Equal(weeklyReset, view.Weekly.ResetsAt);          // the weekly window is untouched
    }

    [Fact]
    public void Panel_AClosedSession_SaysIngenAktivSession_WithNoResetTime_AndNoFullTimeBar()
    {
        PanelTextResult text = PanelText.Compose(IdleView(TimeSpan.FromHours(10)), Now, Utc);

        Assert.Equal("Ingen aktiv session", text.Session.ResetHeader);
        Assert.Equal("startar när kontot används", text.Session.TidLabel);
        Assert.DoesNotContain("återställs", text.Session.ResetHeader);
        Assert.DoesNotContain("% av", text.Session.TidLabel);           // not "100 % av 5 h har gått"
        Assert.Equal("Inget förbrukat ännu", text.Session.KvotLabel);
        Assert.Contains("återställs", text.Weekly.ResetHeader);          // the week still counts down
    }

    [Fact]
    public void Panel_AnOpenSession_StillShowsItsResetAndTheTimeBar()
    {
        QuotaView open = TimelineViews.Open();

        PanelTextResult text = PanelText.Compose(open, TimelineViews.Now, Utc);

        Assert.StartsWith("återställs om 3 h", text.Session.ResetHeader);
        Assert.Contains("% av 5 h har gått", text.Session.TidLabel);
    }

    [Fact]
    public void ClosedWindow_IsRecognisedOnlyWithoutADeadline()
    {
        Assert.True(PanelText.IsClosed(ClosedSession()));
        // a window that is merely Measuring for the same reason but still has a (stale) deadline is not "closed"
        Assert.False(PanelText.IsClosed(ClosedSession() with { ResetsAt = Now.AddHours(-1) }));
        Assert.False(PanelText.IsClosed(TimelineViews.Safe(WindowKind.Session, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Card_AClosedSession_ReadsIngenAktivSession_WithAnEmptyBar()
    {
        AccountCard card = AccountCards.Compose(new CardInput("a", "A", null, IdleView(TimeSpan.FromHours(10))), Now, Utc);

        Assert.Equal("ingen aktiv session", card.Session.Text);
        Assert.Equal(0.0, card.Session.Fraction);
    }

    // ---- bug b: the header of an idle account ----

    [Fact]
    public void IdleAccount_HeaderSaysIngenAktivitetSedan_WithTheDay()
    {
        string? header = PanelText.ComposeIdleHeader(IdleView(TimeSpan.FromHours(10) + TimeSpan.FromMinutes(39)), Now, Utc);

        Assert.Equal("Ingen aktivitet sedan igår kl 23:21", header);
    }

    [Fact]
    public void IdleAccount_ThatLastChangedToday_SaysJustTheClock()
    {
        Assert.Equal("Ingen aktivitet sedan kl 08:00", PanelText.ComposeIdleHeader(IdleView(TimeSpan.FromHours(2)), Now, Utc));
    }

    [Fact]
    public void AnAccountWithAnOpenSession_KeepsTheDataAgeHeader()
    {
        Assert.Null(PanelText.ComposeIdleHeader(TimelineViews.Open(), TimelineViews.Now, Utc));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("needslogin")]
    [InlineData("loading")]
    public void OnlyALiveIdleAccount_GetsTheIdleHeader_FailuresKeepTheirOwnLines(string kind)
    {
        QuotaView view = kind switch
        {
            "stale" => IdleView(TimeSpan.FromHours(10), Freshness.Stale),
            "unknown" => IdleView(TimeSpan.FromHours(10), Freshness.Unknown),
            "needslogin" => IdleView(TimeSpan.FromHours(10)) with { NeedsLogin = true },
            _ => IdleView(TimeSpan.FromHours(10)) with { Loading = true },
        };

        Assert.Null(PanelText.ComposeIdleHeader(view, Now, Utc));
    }
}

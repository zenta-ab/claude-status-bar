using ClaudeStatusBar.Data;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// First-class support for accounts that have no 5 h limit (some "Ultra" plans since 2026-10-31): get_usage answers
/// `five_hour: null` with only weekly entries in limits[], at any weekly percentage. The flag is read per poll
/// (UsageSnapshot.NoSessionLimit), travels to QuotaView, and flips back by itself when a session entry reappears. The
/// session keeps its closed-window values (freshness counts the reply as live); the weekly window drives every
/// "which window matters" choice; the panel shows one muted line instead of the session section.
/// </summary>
public class NoSessionLimitTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateTimeOffset T0 = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset WeeklyReset = T0.AddDays(3);

    static string Raw(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A reply with NOVEL microseconds in resets_at (what the server mostly sends): the tracker's fingerprint is that string, so every poll is "accepted" even when the percentage has not moved. See NoLimitCached for the other case.</summary>
    static UsageSnapshot NoLimit(DateTimeOffset now, double weeklyPct) =>
        new(0.0, null, weeklyPct, Raw(WeeklyReset.AddTicks((long)(now - T0).TotalSeconds * 20)), now, "max", SessionIsActive: false, WeeklyIsActive: true, NoSessionLimit: true);

    /// <summary>A reply with the IDENTICAL resets_at string every time (a cached/replica reply): the weekly tracker dedups all but the first, so nothing is ever "accepted" again.</summary>
    static UsageSnapshot NoLimitCached(DateTimeOffset now, double weeklyPct) =>
        new(0.0, null, weeklyPct, Raw(WeeklyReset), now, "max", SessionIsActive: false, WeeklyIsActive: true, NoSessionLimit: true);

    static UsageSnapshot WithSession(DateTimeOffset now, DateTimeOffset sessionReset, double weeklyPct = 30.0) =>
        new(20.0, Raw(sessionReset), weeklyPct, Raw(WeeklyReset), now, "max");

    /// <summary>Polls every 150 s for `count` polls with the weekly percentage creeping up; returns the model and the last instant/mono.</summary>
    static (QuotaModel Model, DateTimeOffset Now, long Mono) PollNoLimit(int count, double startPct = 10.0, double stepPct = 0.1)
    {
        var model = new QuotaModel();
        DateTimeOffset now = T0;
        long mono = 0;
        for (int i = 0; i < count; i++)
        {
            now = T0.AddSeconds(150 * i);
            mono = 150_000L * i;
            model.Ingest(NoLimit(now, startPct + stepPct * i), now, mono);
        }
        return (model, now, mono);
    }

    // ---- the model ----

    [Fact]
    public void Model_NoSessionLimit_FlagsTheView_AndSaysSoInsteadOfNothingUsedYet()
    {
        (QuotaModel model, DateTimeOffset now, long mono) = PollNoLimit(3);

        QuotaView view = model.Evaluate(now, mono);

        Assert.True(view.NoSessionLimit);
        Assert.Equal(QuotaState.Measuring, view.Session.State);
        Assert.Null(view.Session.ResetsAt);
        Assert.Null(view.Session.UsedPct);
        Assert.Equal("Ingen 5-timmarsgräns", view.Session.MeasuringReason);
    }

    [Fact]
    public void Model_ABareClosedSession_IsStillTheOldNothingUsedYet()
    {
        var model = new QuotaModel();
        model.Ingest(new UsageSnapshot(0.0, null, 20.0, Raw(WeeklyReset), T0, "max", SessionIsActive: false, WeeklyIsActive: true), T0, 0);

        QuotaView view = model.Evaluate(T0, 0);

        Assert.False(view.NoSessionLimit);
        Assert.Equal("Inget förbrukat ännu", view.Session.MeasuringReason);
    }

    [Fact]
    public void Model_NoSessionLimit_LastChangedAtIsTheWeeklyWindowsOwn_NotNullForever()
    {
        (QuotaModel model, DateTimeOffset now, long mono) = PollNoLimit(5);

        QuotaView view = model.Evaluate(now, mono);

        Assert.Equal(now, view.LastChangedAt);
        Assert.Equal(Freshness.Live, view.Freshness);
        Assert.Equal(now, view.LastActivityAt); // the weekly percentage rose on the very last poll
    }

    [Fact]
    public void Model_NoSessionLimit_FlatPercentage_NovelFingerprints_NeverClaimsActivitySinceTheLastPoll()
    {
        // 12 % for 6 h, a novel resets_at string on every poll: the weekly tracker accepts each reply, so LastChangedAt
        // is "just now" -- but nothing was used, and the header must not say "Ingen aktivitet sedan <now>".
        var model = new QuotaModel();
        DateTimeOffset now = T0;
        long mono = 0;
        for (int i = 0; i <= 144; i++) // every 150 s for 6 h
        {
            now = T0.AddSeconds(150 * i);
            mono = 150_000L * i;
            model.Ingest(NoLimit(now, 12.0), now, mono);
        }

        QuotaView view = model.Evaluate(now, mono);

        Assert.Equal(Freshness.Live, view.Freshness);
        Assert.Equal(now, view.LastChangedAt);                   // the data age: this poll
        Assert.Null(view.LastActivityAt);                        // no rise ever observed (the first sample only re-baselines)
        Assert.Null(PanelText.ComposeIdleHeader(view, now, Utc)); // -> the ordinary "Uppdaterad för …" header stands
    }

    [Fact]
    public void Model_NoSessionLimit_AfterAnObservedRise_TheHeaderQuotesThatRise_NotTheLastPoll()
    {
        var model = new QuotaModel();
        DateTimeOffset rise = T0.AddMinutes(10);
        DateTimeOffset now = T0;
        long mono = 0;
        for (int i = 0; i <= 96; i++) // every 150 s for 4 h; the percentage steps up once, at +10 min
        {
            now = T0.AddSeconds(150 * i);
            mono = 150_000L * i;
            model.Ingest(NoLimit(now, now >= rise ? 13.0 : 12.0), now, mono);
        }

        QuotaView view = model.Evaluate(now, mono);

        Assert.Equal(rise, view.LastActivityAt);
        Assert.Equal(now, view.LastChangedAt);
        string? header = PanelText.ComposeIdleHeader(view, now, Utc);
        Assert.Equal($"Ingen aktivitet sedan {TimeText.PointInTime(rise, now, Utc)}", header);
    }

    [Fact]
    public void Model_NoSessionLimit_IdenticalFingerprintsForHours_StaysLive_WithASaneCadence()
    {
        // The common production case: the same resets_at string on every reply. After the first, the weekly tracker
        // dedups everything; the session is closed on every poll -- each reply is still proof of liveness.
        var model = new QuotaModel();
        for (int i = 0; i <= 144; i++) // every 150 s for 6 h
        {
            DateTimeOffset now = T0.AddSeconds(150 * i);
            long mono = 150_000L * i;
            model.Ingest(NoLimitCached(now, 12.0), now, mono);

            QuotaView view = model.Evaluate(now, mono);
            Assert.Equal(Freshness.Live, view.Freshness);
            if (i >= 5) // the first sample's re-baselining rise counts as "burning" for 10 min, as for any account
            {
                Assert.Equal(TimeSpan.FromSeconds(150), view.PollInterval);
                Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(now));
            }
        }

        DateTimeOffset end = T0.AddSeconds(150 * 144);
        QuotaView last = model.Evaluate(end, 150_000L * 144);
        Assert.True(last.NoSessionLimit);
        Assert.Equal(T0, last.LastChangedAt);     // the only accepted weekly sample: the first one
        Assert.Null(last.LastActivityAt);
        Assert.Null(PanelText.ComposeIdleHeader(last, end, Utc));
    }

    [Fact]
    public void Model_NoSessionLimit_IdenticalFingerprints_GoStaleWhenRepliesStop()
    {
        // Liveness is still the transport: after the last reply, 3 poll intervals (450 s) is Stale, 10 (1500 s) Unknown.
        var model = new QuotaModel();
        DateTimeOffset last = T0;
        long lastMono = 0;
        for (int i = 0; i <= 8; i++) // 20 min of replies at the idle cadence, then silence
        {
            last = T0.AddSeconds(150 * i);
            lastMono = 150_000L * i;
            model.Ingest(NoLimitCached(last, 12.0), last, lastMono);
        }

        Assert.Equal(Freshness.Live, model.Evaluate(last.AddSeconds(440), lastMono + 440_000).Freshness);
        Assert.Equal(Freshness.Stale, model.Evaluate(last.AddSeconds(460), lastMono + 460_000).Freshness);
        Assert.Equal(Freshness.Unknown, model.Evaluate(last.AddSeconds(1510), lastMono + 1_510_000).Freshness);
    }

    [Fact]
    public void Model_NoSessionLimit_SeverityComesFromTheWeeklyWindow()
    {
        // Days into a week at a modest pace: the weekly window commits a verdict; the session never has one.
        var model = new QuotaModel();
        DateTimeOffset start = WeeklyReset.AddDays(-5);
        DateTimeOffset now = start;
        long mono = 0;
        for (int i = 0; i < 60; i++)
        {
            now = start.AddMinutes(30 * i);
            mono = 30L * 60_000 * i;
            model.Ingest(NoLimit(now, 10.0 + 0.1 * i), now, mono);
        }

        QuotaView view = model.Evaluate(now, mono);

        Assert.NotEqual(QuotaState.Measuring, view.Weekly.State);
        Assert.Equal(view.Weekly.State, view.IconSeverity);
        Assert.Same(view.Weekly, view.Driver);
    }

    [Fact]
    public void Model_NoSessionLimit_WithASpentWeek_BlocksUntilTheWeeklyReset()
    {
        var model = new QuotaModel();
        DateTimeOffset start = WeeklyReset.AddDays(-2);
        for (int i = 0; i < 6; i++)
        {
            DateTimeOffset t = start.AddMinutes(5 * i);
            model.Ingest(NoLimit(t, 100.0), t, 300_000L * i);
        }
        DateTimeOffset now = start.AddMinutes(25);

        QuotaView view = model.Evaluate(now, 25 * 60_000L);

        Assert.True(view.NoSessionLimit);
        Assert.Equal(QuotaState.Spent, view.IconSeverity);
        Assert.Equal(WeeklyReset, view.BlockedUntil!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Model_NoSessionLimit_FlipsBackWhenASessionEntryReappears()
    {
        (QuotaModel model, DateTimeOffset now, long mono) = PollNoLimit(3);
        Assert.True(model.Evaluate(now, mono).NoSessionLimit);

        DateTimeOffset later = now.AddSeconds(150);
        long laterMono = mono + 150_000;
        model.Ingest(WithSession(later, later.AddHours(4)), later, laterMono);
        QuotaView view = model.Evaluate(later, laterMono);

        Assert.False(view.NoSessionLimit);
        Assert.Equal(later.AddHours(4), view.Session.ResetsAt);
        Assert.NotEqual("Ingen 5-timmarsgräns", view.Session.MeasuringReason);
    }

    [Fact]
    public void Model_SessionDisappearsInProcess_StaleSessionDeadlineDoesNotKeepPollingEvery30Seconds()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = T0.AddHours(4);
        model.Ingest(WithSession(T0, sessionReset), T0, 0);

        // Long after the old session deadline, the account answers without any session: the old deadline is history.
        DateTimeOffset later = sessionReset.AddHours(2);
        long mono = (long)(later - T0).TotalMilliseconds;
        model.Ingest(NoLimit(later, 30.0), later, mono);

        Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(later));
        Assert.Equal(TimeSpan.FromSeconds(150), model.Evaluate(later, mono).PollInterval);
    }

    [Fact]
    public void Model_SpentSessionThenNoSessionLimit_InProcess_IsNotPinnedAtTheSlowSpentInterval()
    {
        // Eight 100 % session samples commit a Spent session hold (300 s). The account then starts answering without any
        // session: that hold is history -- it only steps on accepted samples, and there will be none -- and must not
        // keep the poll interval at 300 s (nor must the old session's recent rise count as "burning").
        var model = new QuotaModel();
        DateTimeOffset sessionReset = T0.AddHours(1);
        for (int i = 0; i < 8; i++)
        {
            DateTimeOffset t = T0.AddMinutes(5 * i);
            model.Ingest(new UsageSnapshot(100.0, Raw(sessionReset.AddTicks(i * 17)), 30.0, Raw(WeeklyReset), t, "max"), t, 300_000L * i);
        }
        DateTimeOffset spentAt = T0.AddMinutes(35);
        Assert.Equal(TimeSpan.FromSeconds(300), model.NextPollDelay(spentAt)); // the premise: a genuinely Spent session

        DateTimeOffset now = T0.AddMinutes(40);
        long mono = 40 * 60_000L;
        model.Ingest(NoLimit(now, 30.0), now, mono);

        Assert.True(model.Evaluate(now, mono).NoSessionLimit);
        Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(now));

        // Past the old session deadline as well: still the idle cadence, never 30 s "awaiting reset" for a session that is gone.
        DateTimeOffset later = sessionReset.AddMinutes(30);
        long laterMono = (long)(later - T0).TotalMilliseconds;
        model.Ingest(NoLimit(later, 30.0), later, laterMono);

        Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(later));
        Assert.Equal(TimeSpan.FromSeconds(150), model.Evaluate(later, laterMono).PollInterval);
    }

    [Fact]
    public void Model_NoSessionLimit_StaysLiveForHours_WithTheWeeklyMoving()
    {
        var model = new QuotaModel();
        for (int i = 0; i <= 96; i++) // every 150 s for 4 h
        {
            DateTimeOffset now = T0.AddSeconds(150 * i);
            long mono = 150_000L * i;
            model.Ingest(NoLimit(now, 10.0 + 0.05 * i), now, mono);

            Assert.Equal(Freshness.Live, model.Evaluate(now, mono).Freshness);
        }
    }

    [Fact]
    public void Model_NoSessionLimit_StaysLiveForHours_WithTheWeeklyPercentageFlat_ButNovelFingerprints()
    {
        var model = new QuotaModel();
        for (int i = 0; i <= 96; i++)
        {
            DateTimeOffset now = T0.AddSeconds(150 * i);
            long mono = 150_000L * i;
            model.Ingest(NoLimit(now, 12.0), now, mono);

            Assert.Equal(Freshness.Live, model.Evaluate(now, mono).Freshness);
        }
    }

    // ---- end to end: the real get_usage shape -> parser -> model -> view ----

    /// <summary>The real reply, trimmed: `five_hour: null`, limits[] holding only weekly_all and weekly_scoped, the resets_at with its jittery microseconds.</summary>
    static string NoLimitJson(double weeklyPct, DateTimeOffset weeklyReset) => $$"""
        {
          "response": {
            "subscription_type": "max",
            "rate_limits": {
              "five_hour": null,
              "seven_day": { "utilization": {{weeklyPct.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "resets_at": "{{Raw(weeklyReset)}}" },
              "limits": [
                { "kind": "weekly_all", "group": "weekly", "percent": {{weeklyPct.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "resets_at": "{{Raw(weeklyReset)}}", "is_active": true },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 4, "resets_at": "{{Raw(weeklyReset)}}", "is_active": false }
              ]
            }
          }
        }
        """;

    [Fact]
    public void EndToEnd_RealNoLimitJson_ThroughParserAndModel_FlagsTheViewAndDrivesByTheWeek()
    {
        var model = new QuotaModel();
        DateTimeOffset now = T0;
        long mono = 0;
        for (int i = 0; i < 6; i++)
        {
            now = T0.AddSeconds(150 * i);
            mono = 150_000L * i;
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
                NoLimitJson(10.0 + 0.5 * i, WeeklyReset.AddTicks(i * 23)));
            UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);
            Assert.Null(error);
            Assert.NotNull(snapshot);
            model.Ingest(snapshot!, now, mono);
        }

        QuotaView view = model.Evaluate(now, mono);

        Assert.True(view.NoSessionLimit);
        Assert.Equal(Freshness.Live, view.Freshness);
        Assert.Same(view.Weekly, view.Driver);
        Assert.Equal(QuotaModel.NoSessionLimitText, view.Session.MeasuringReason);
        Assert.Equal(12.5, view.Weekly.UsedPct);
        Assert.Equal(now, view.LastActivityAt); // the weekly percentage rose on the last poll
        Assert.Equal(PanelText.NoSessionLimitPanelLine, PanelText.Compose(view, now, Utc).NoSessionLimitLine);
    }

    // ---- panel text ----

    static WindowView Win(WindowKind kind, QuotaState state, double pct, DateTimeOffset? resets, string? reason = null) =>
        new(kind, kind == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes, pct, resets, state,
            state == QuotaState.Measuring ? null : 0.01, state == QuotaState.Measuring ? null : 1, state == QuotaState.Measuring ? null : pct + 10,
            null, TimeSpan.Zero, reason);

    static QuotaView NoLimitView(WindowView weekly, Freshness freshness = Freshness.Live, DateTimeOffset? blocked = null, DateTimeOffset? activity = null) => new(
        QuotaModel.ClosedWindowView(WindowKind.Session, QuotaWindows.SessionMinutes, MeasuringReasonCode.NoSessionLimit), weekly, freshness,
        T0.AddMinutes(-3), T0.AddSeconds(-2), TimeSpan.FromSeconds(150), null, weekly.State, blocked, NoSessionLimit: true, LastActivityAt: activity);

    static QuotaView NormalView() => new(
        Win(WindowKind.Session, QuotaState.Safe, 18, T0.AddHours(3)), Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)),
        Freshness.Live, T0, T0, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

    [Fact]
    public void Panel_NoSessionLimit_ReplacesTheSessionSectionWithOneLine()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)));

        PanelTextResult text = PanelText.Compose(view, T0, Utc);

        Assert.Equal("Ingen 5-timmarsgräns – bara veckokvoten gäller", text.NoSessionLimitLine);
    }

    [Fact]
    public void Panel_NormalAccount_HasNoNoLimitLine()
    {
        Assert.Null(PanelText.Compose(NormalView(), T0, Utc).NoSessionLimitLine);
    }

    [Fact]
    public void Panel_NoSessionLimit_StatusBoxQuotesTheWeek()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)));

        StatusBoxText box = PanelText.Compose(view, T0, Utc).StatusBox;

        Assert.Equal("✓ Veckokvoten räcker till reset", box.Line1);
        Assert.StartsWith("Veckan landar på", box.Line2);
    }

    [Fact]
    public void Panel_NoSessionLimit_SpentWeek_SaysVeckokvotenIsSlut()
    {
        DateTimeOffset reset = T0.AddDays(2);
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Spent, 100, reset), blocked: reset);

        StatusBoxText box = PanelText.Compose(view, T0, Utc).StatusBox;

        Assert.Equal("Veckokvoten är slut", box.Line1);
        Assert.Equal(PanelColorRole.Dead, box.Role);
    }

    [Fact]
    public void Panel_NoSessionLimit_MeasuringWeek_IsTheDriverOfTheMeasuringBox()
    {
        // Both windows Measuring: the default driver used to be the session ("Hämtar…"/"okänt"); with no session it is the week.
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Measuring, 12, T0.AddDays(4), "För lite förbrukning ännu…"));

        StatusBoxText box = PanelText.Compose(view, T0, Utc).StatusBox;

        Assert.Equal("Mäter takt…", box.Line1);
        Assert.StartsWith("12 % använt · återställs", box.Line2);
    }

    [Fact]
    public void Driver_NoSessionLimit_IsTheWeek_EvenOnATie_AndANormalAccountKeepsSessionOnATie()
    {
        QuotaView noLimit = NoLimitView(Win(WindowKind.Weekly, QuotaState.Measuring, 12, T0.AddDays(4)));
        Assert.Same(noLimit.Weekly, noLimit.Driver);

        QuotaView tie = NormalView();
        Assert.Same(tie.Session, tie.Driver);
    }

    [Fact]
    public void OtherAccountRow_NoSessionLimit_SpeaksAboutTheWeek()
    {
        WindowView weekly = Win(WindowKind.Weekly, QuotaState.DryEarly, 70, T0.AddDays(2)) with { DepletesAt = T0.AddHours(30) };
        QuotaView view = NoLimitView(weekly);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(1, "Ultra", view, T0, Utc);

        Assert.StartsWith("veckan tar slut", row.Line);
        Assert.Equal(PanelColorRole.Crit, row.Role);
    }

    [Fact]
    public void Tooltip_NoSessionLimit_QuotesTheWeeksReset_NotTheClosedSession()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Measuring, 12, T0.AddDays(2)));

        string tooltip = IconSlot.BuildTooltip(view, accountLabel: null);

        Assert.Contains("Mäter takt · reset", tooltip);
    }

    // ---- the header ----

    [Fact]
    public void Header_NoSessionLimit_AndLive_SaysNoActivitySince_FromTheLastObservedWeeklyRise()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)), activity: T0.AddHours(-3));

        string? header = PanelText.ComposeIdleHeader(view, T0, Utc);

        Assert.NotNull(header);
        Assert.StartsWith("Ingen aktivitet sedan", header);
    }

    [Fact]
    public void Header_NoSessionLimit_WithNoObservedRise_FallsBackToTheDataAge_NotAMadeUpActivityTime()
    {
        // LastChangedAt is set (the last poll) but no rise has been seen: the idle header must not borrow it.
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)));

        Assert.NotNull(view.LastChangedAt);
        Assert.Null(view.LastActivityAt);
        Assert.Null(PanelText.ComposeIdleHeader(view, T0, Utc));
    }

    [Fact]
    public void Header_NoSessionLimit_WhenStale_KeepsTheDataAgeHeader()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)), Freshness.Stale);

        Assert.Null(PanelText.ComposeIdleHeader(view, T0, Utc));
    }

    // ---- compact cards ----

    [Fact]
    public void Card_NoSessionLimit_SessionRowIsMutedTextWithoutABar_WeekRowIsNormal()
    {
        QuotaView view = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)));

        AccountCard card = AccountCards.Compose(new CardInput("u", "Ultra", "Ultra · personlig organisation", view), T0, Utc);

        Assert.Equal("Session", card.Session.Caption);
        Assert.Equal("Ingen 5-timmarsgräns", card.Session.Text);
        Assert.True(card.Session.NoBar);
        Assert.False(card.Week.NoBar);
        Assert.StartsWith("12 %", card.Week.Text);
        Assert.Equal("✓ Veckokvoten räcker till reset", card.Verdict);
    }

    [Fact]
    public void Card_NormalAccount_SessionRowStillHasABar()
    {
        AccountCard card = AccountCards.Compose(new CardInput("a", "Privat", null, NormalView()), T0, Utc);

        Assert.False(card.Session.NoBar);
    }

    // ---- the full panel layout ----

    [Fact]
    public void FullPanel_NoSessionLimitColumn_KeepsTheRowsAlignedWithANormalColumn()
    {
        using var panel = new PanelForm();
        QuotaView noLimit = NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4)));

        panel.UpdateFull(new[]
        {
            new PanelModel("n", noLimit, "Ultra", "Ultra · personlig organisation", Array.Empty<OtherAccountRow>(), false, null, false),
            new PanelModel("a", NormalView(), "Privat", "Max", Array.Empty<OtherAccountRow>(), false, null, false),
        }, "n", false);

        IReadOnlyList<PanelForm.FullRowYs> rows = panel.FullRows;
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.SessionY).Distinct());
        Assert.Single(rows.Select(r => r.WeeklyY).Distinct());
        Assert.Single(rows.Select(r => r.FooterY).Distinct());
        Assert.Single(rows.Select(r => r.Height).Distinct());
    }

    [Fact]
    public void Panel_NoSessionLimit_IsShorterThanANormalPanel_BecauseTheSessionSectionIsOneLine()
    {
        using var noLimit = new PanelForm();
        using var normal = new PanelForm();
        noLimit.UpdateFull(new[] { new PanelModel("n", NoLimitView(Win(WindowKind.Weekly, QuotaState.Safe, 12, T0.AddDays(4))), "Ultra", null, Array.Empty<OtherAccountRow>(), false, null, false) }, "n", false);
        normal.UpdateFull(new[] { new PanelModel("a", NormalView(), "Privat", null, Array.Empty<OtherAccountRow>(), false, null, false) }, "a", false);

        Assert.True(noLimit.FullPanelHeights[0] < normal.FullPanelHeights[0] - 80f,
            $"{noLimit.FullPanelHeights[0]} vs {normal.FullPanelHeights[0]}");
    }

    // ---- the availability timeline ----

    [Fact]
    public void Timeline_NoSessionLimit_BlocksOnlyOnTheWeek()
    {
        DateTimeOffset now = TimelineViews.Now;
        DateTimeOffset reset = now.AddHours(8);
        QuotaView open = NoLimitView(TimelineViews.Safe(WindowKind.Weekly, TimeSpan.FromDays(2)));
        QuotaView spent = NoLimitView(TimelineViews.Spent(WindowKind.Weekly, TimeSpan.FromHours(8)), blocked: reset);

        IReadOnlyList<AvailabilitySegment> openSegments = AvailabilityTimeline.SegmentsFor(open, now, now.AddHours(24));
        IReadOnlyList<AvailabilitySegment> spentSegments = AvailabilityTimeline.SegmentsFor(spent, now, now.AddHours(24));

        Assert.Single(openSegments);
        Assert.Equal(reset, spentSegments[0].End);
        Assert.NotEqual(openSegments[0].Kind, spentSegments[0].Kind);
    }

    // ---- the CSV log ----

    [Fact]
    public async Task Csv_NoSessionLimit_WritesBlankSessionColumns_AndReplayReadsNoSessionPct()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-nolimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sink = new DiskLogSink(dir);
        try
        {
            sink.EnqueueCsv(NoLimit(DateTimeOffset.UtcNow, 12.0), TimeSpan.FromMilliseconds(5));
            sink.EnqueueCsv(new UsageSnapshot(18.0, Raw(DateTimeOffset.UtcNow.AddHours(3)), 12.0, Raw(WeeklyReset), DateTimeOffset.UtcNow, "max"), TimeSpan.FromMilliseconds(5));
        }
        finally
        {
            await sink.DisposeAsync(TimeSpan.FromSeconds(3));
        }

        try
        {
            string file = Directory.EnumerateFiles(dir, "window-shape-*.csv").Single();
            string[] lines = File.ReadAllLines(file);
            string[] noLimit = lines[1].Split(',');
            string[] normal = lines[2].Split(',');

            Assert.Equal("", noLimit[2]);   // five_hour_pct
            Assert.Equal("", noLimit[3]);   // five_hour_resets_at_raw
            Assert.Equal("12", noLimit[4]); // seven_day_pct is untouched
            Assert.Equal("18", normal[2]);

            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(file);
            Assert.Null(rows[0].SessionPct);
            Assert.Equal(18.0, rows[1].SessionPct);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- statistics ----

    [Fact]
    public void Statistics_NoSessionLimit_DropsTheSessionOnlyTilesAndHeatmap_AndSaysWhy()
    {
        var data = new StatisticsDataLoader.LoadedData(
            StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), Array.Empty<HourlyRollupCsv.Row>(), "acct", Utc),
            Array.Empty<CycleArchiveCsv.Row>(), Array.Empty<StatisticsDataLoader.RecentCycle>());

        StatisticsWindowText normal = StatisticsText.Compose(data, StatisticsPeriod.Year, Utc, T0);
        StatisticsWindowText noLimit = StatisticsText.Compose(data, StatisticsPeriod.Year, Utc, T0, noSessionLimit: true);

        Assert.Null(normal.NoSessionLimitNote);
        Assert.Contains(normal.Tiles, t => t.Title == "Mest aktiva veckodagen");
        Assert.Equal("Ingen 5-timmarsgräns på det här kontot", noLimit.NoSessionLimitNote);
        Assert.DoesNotContain(noLimit.Tiles, t => t.Title is "Mest aktiva veckodagen" or "Mest aktiva timmen" or "Nådde taket");
        Assert.Contains(noLimit.Tiles, t => t.Title == "Veckobudget använd");
        Assert.Empty(noLimit.Heatmap);
        Assert.Null(noLimit.CeilingRecommendation);
    }

    // ---- the plan label ----

    static AccountIdentity Identity(string? planDisplayName, string? org = null) => new(
        "uuid-1", null, org, null, null, null, "alex@example.com", null, planDisplayName);

    [Fact]
    public void Label_PlanDisplayName_BeatsTitleCasedSubscriptionType_ButNotAnOverrideOrTheOrganisation()
    {
        Assert.Equal("Ultra", AccountLabel.Resolve(new AccountLabelInput(null, Identity("Ultra"), "max"), 0));
        Assert.Equal("Max", AccountLabel.Resolve(new AccountLabelInput(null, Identity(null), "max"), 0));
        Assert.Equal("Mitt", AccountLabel.Resolve(new AccountLabelInput("Mitt", Identity("Ultra"), "max"), 0));
        Assert.Equal("Acme AB", AccountLabel.Resolve(new AccountLabelInput(null, Identity("Ultra", "Acme AB"), "max"), 0));
    }

    [Fact]
    public void Label_Disambiguate_UsesThePlanDisplayName_WhenTwoAccountsShareAnOrganisation()
    {
        // Same organisation and the same subscription_type ("max"): only planDisplayName tells them apart.
        var accounts = new[]
        {
            new AccountLabelInput(null, Identity("Ultra", "Acme"), "max"),
            new AccountLabelInput(null, Identity(null, "Acme"), "max"),
        };

        IReadOnlyList<string> labels = AccountLabel.Disambiguate(accounts);

        Assert.Equal("Acme (Ultra)", labels[0]);
        Assert.Equal("Acme (Max)", labels[1]);
    }

    [Fact]
    public void Subtitle_ShowsThePlanDisplayName()
    {
        const string personalOrg = "alex@example.com" + "'s Organization";
        Assert.Equal("Ultra · personlig organisation", PanelText.ComposeAccountSubtitle("Privat", "max", personalOrg, planDisplayName: "Ultra"));
        Assert.Equal("Max · personlig organisation", PanelText.ComposeAccountSubtitle("Privat", "max", personalOrg));
    }

    [Fact]
    public void Identity_ReadsPlanDisplayNameFromOauthAccount()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".claude.json"),
                """{ "oauthAccount": { "accountUuid": "11111111-2222-3333-4444-555555555555", "planDisplayName": "Ultra" } }""");

            Assert.Equal("Ultra", AccountIdentity.ReadFrom(dir)!.PlanDisplayName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- demo ----

    [Fact]
    public void Demo_HasNoSessionLimitStates()
    {
        QuotaView normal = DemoQuotaSource.Build(T0).Single(s => s.Key == "no_session_limit").View;
        QuotaView spent = DemoQuotaSource.Build(T0).Single(s => s.Key == "no_session_limit_spent").View;

        Assert.True(normal.NoSessionLimit);
        Assert.True(spent.NoSessionLimit);
        Assert.Equal(QuotaState.Spent, spent.IconSeverity);
        Assert.NotNull(spent.BlockedUntil);
        Assert.Contains(MultiAccountDemoSource.Build(T0).Single(m => m.Key == "multi_no_limit").Accounts, a => a.View.NoSessionLimit);
    }
}

using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/multi-account.md "Loading": an account that has not answered yet is "Hämtar kvoten…", not an
/// error. The state itself (Model/LoadingTracker), how it looks (icon params + frames, tooltip, panel
/// status box, other-accounts row), and -- in AccountRuntimeTests -- how a real runtime moves through it.
/// </summary>
public class LoadingStateTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test/+1", TimeSpan.FromHours(1), "Test/+1", "Test/+1");
    static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    static QuotaView LoadingView() => QuotaView.Initial with { Loading = true };

    // ---- the tracker's transitions ----

    [Fact]
    public void ANewTracker_IsLoading_UntilTheTimeout()
    {
        var tracker = new LoadingTracker(startMono: 1_000);

        Assert.True(tracker.IsLoading(1_000));
        Assert.True(tracker.IsLoading(1_000 + 29_999));
        Assert.False(tracker.IsLoading(1_000 + 30_000)); // 30 s: the normal Unknown rules take over
        Assert.False(tracker.IsLoading(1_000 + 3_600_000));
        Assert.Equal(TimeSpan.FromSeconds(30), LoadingTracker.DefaultTimeout);
    }

    [Fact]
    public void TheFirstResult_EndsLoading_ForGood()
    {
        var tracker = new LoadingTracker(startMono: 0);

        tracker.End(); // a success, or a failure: either is a result

        Assert.False(tracker.IsLoading(1));
        Assert.False(tracker.IsLoading(0));
    }

    [Fact]
    public void ARestart_BeginsLoadingAgain_WithAFreshTimeout()
    {
        var tracker = new LoadingTracker(startMono: 0);
        tracker.End();
        Assert.False(tracker.IsLoading(5));

        tracker.Restart(monoMs: 100_000);

        Assert.True(tracker.IsLoading(100_000));
        Assert.True(tracker.IsLoading(129_999));
        Assert.False(tracker.IsLoading(130_000));
    }

    [Fact]
    public void ACustomTimeout_IsHonoured()
    {
        var tracker = new LoadingTracker(startMono: 0, timeout: TimeSpan.FromMilliseconds(500));

        Assert.True(tracker.IsLoading(499));
        Assert.False(tracker.IsLoading(500));
    }

    // ---- icon ----

    [Fact]
    public void LoadingIcon_IsItsOwnGlyph_NotTheUnknownOutline()
    {
        QuotaIconParams loading = QuotaIconParams.Build(LoadingView(), 16, taskbarDark: true, Now);
        QuotaIconParams unknown = QuotaIconParams.Build(QuotaView.Initial, 16, taskbarDark: true, Now);

        Assert.True(loading.Loading);
        Assert.False(loading.Outline);   // the Outline glyph is the one with the "!"
        Assert.True(unknown.Outline);
        Assert.False(unknown.Loading);
        Assert.NotEqual(loading, unknown);
    }

    [Fact]
    public void LoadingFrame_ChangesEveryFrameInterval_AndWrapsAroundTheFixedSet()
    {
        DateTimeOffset t0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var seen = new HashSet<int>();
        int previous = -1;
        for (int i = 0; i < LoadingFrames.FrameCount * 3; i++)
        {
            int frame = QuotaIconParams.Build(LoadingView(), 16, true, t0.AddMilliseconds(i * LoadingFrames.FrameIntervalMs)).LoadingFrame;
            Assert.InRange(frame, 0, LoadingFrames.FrameCount - 1);
            Assert.NotEqual(previous, frame);
            seen.Add(frame);
            previous = frame;
        }
        Assert.Equal(LoadingFrames.FrameCount, seen.Count);
        Assert.Equal(8, LoadingFrames.FramesPerSecond); // "max ~8 fps"
    }

    [Fact]
    public void WithinOneFrameInterval_TheParamsAreEqual_SoNothingIsReRendered()
    {
        DateTimeOffset t0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        QuotaIconParams a = QuotaIconParams.Build(LoadingView(), 16, true, t0);
        QuotaIconParams b = QuotaIconParams.Build(LoadingView(), 16, true, t0.AddMilliseconds(LoadingFrames.FrameIntervalMs - 1));

        Assert.Equal(a, b);
    }

    [Fact]
    public void LoadingFrames_AreRenderedOnce_AndCached()
    {
        byte[] first = LoadingFrames.GetIcoBytes(20, 3);
        byte[] again = LoadingFrames.GetIcoBytes(20, 3);

        Assert.Same(first, again);                   // cached, not re-rendered
        Assert.True(first.Length > 22);              // a real ICO: header + entry + image
        Assert.Equal(0, first[0]);
        Assert.Equal(1, first[2]);                   // ICO resource type 1
        Assert.NotEqual(first, LoadingFrames.GetIcoBytes(20, 4)); // the arc has moved
    }

    [Fact]
    public void LoadingFrames_TheCacheStaysBounded_NoMatterHowLongItSpins()
    {
        for (int i = 0; i < 1000; i++) LoadingFrames.GetIcoBytes(24, i);

        Assert.True(LoadingFrames.CachedFrames <= LoadingFrames.FrameCount * 8, $"{LoadingFrames.CachedFrames} cached frames"); // 8 frames x a handful of tray sizes
    }

    [Fact]
    public void LoadingGlyph_RendersAtEveryTraySize_WithAnArcThatMoves()
    {
        foreach (int px in new[] { 16, 20, 24, 32 })
        {
            using var a = GaugeRenderer.RenderLoading(px, 0, LoadingFrames.FrameCount);
            using var b = GaugeRenderer.RenderLoading(px, 4, LoadingFrames.FrameCount);
            Assert.Equal(px, a.Width);
            bool differs = false;
            for (int y = 0; y < px && !differs; y++)
                for (int x = 0; x < px && !differs; x++)
                    differs = a.GetPixel(x, y) != b.GetPixel(x, y);
            Assert.True(differs, $"frames 0 and 4 look identical at {px}px");
        }
    }

    // ---- tooltip ----

    [Fact]
    public void Tooltip_Loading_SaysHämtarKvoten_AndIsNotTheErrorText()
    {
        string tooltip = IconSlot.BuildTooltip(LoadingView(), accountLabel: null);

        Assert.Equal("Hämtar kvoten…", tooltip);
        Assert.NotEqual("Kan inte läsa kvoten", tooltip);
    }

    [Fact]
    public void Tooltip_Loading_WithALabel_StaysWithinTheCap()
    {
        string tooltip = IconSlot.BuildTooltip(LoadingView(), accountLabel: "A rather long organisation name AB, quite long");

        Assert.True(tooltip.Length <= 63, tooltip);
        Assert.EndsWith("Hämtar kvoten…", tooltip);
    }

    // ---- panel ----

    [Fact]
    public void StatusBox_Loading_SaysHämtarKvoten_InTheNeutralColour_NotKanInteLäsaKvoten()
    {
        StatusBoxText box = PanelText.Compose(LoadingView(), Now, Tz).StatusBox;

        Assert.Equal("Hämtar kvoten…", box.Line1);
        Assert.Equal(PanelColorRole.Measuring, box.Role);
        Assert.NotEqual("Kan inte läsa kvoten", box.Line1);
        Assert.NotEqual(PanelColorRole.Unknown, box.Role);
    }

    [Fact]
    public void OtherAccountsRow_Loading_SaysHämtar()
    {
        OtherAccountRow row = PanelText.ComposeOtherAccountRow(1, "Team", LoadingView(), Now, Tz);

        Assert.Equal("hämtar…", row.Line);
        Assert.Equal(PanelColorRole.Measuring, row.Role);
    }

    [Fact]
    public void AfterLoading_AnUnreadableAccountIsAnError_AsBefore()
    {
        QuotaView unknown = QuotaView.Initial; // Loading false: the 30 s are over / a failure arrived

        Assert.Equal("Kan inte läsa kvoten", PanelText.Compose(unknown, Now, Tz).StatusBox.Line1);
        Assert.Equal("Kan inte läsa kvoten", IconSlot.BuildTooltip(unknown, null));
        Assert.Equal("går inte att läsa", PanelText.ComposeOtherAccountRow(0, "Team", unknown, Now, Tz).Line);
    }

    [Fact]
    public void NeedsLogin_IsNeverShownAsLoading()
    {
        QuotaView needsLogin = QuotaView.Initial with { NeedsLogin = true };

        Assert.Equal("Inloggningen har gått ut eller saknas", PanelText.Compose(needsLogin, Now, Tz).StatusBox.Line1);
        Assert.False(QuotaIconParams.Build(needsLogin, 16, true, Now).Loading);
    }

    [Fact]
    public void ALoadingAccount_NeverWinsBindingSelection_OverOneWithARealVerdict()
    {
        QuotaView safe = new(
            WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes), WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
            Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);
        var candidates = new[]
        {
            new AccountDisplayPlan.Candidate(true, LoadingView()),
            new AccountDisplayPlan.Candidate(true, safe),
        };

        Assert.Equal(1, AccountDisplayPlan.SelectBinding(candidates, Now));
    }
}

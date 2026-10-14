using System.Drawing;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/forecast-and-states.md "Exhausted": the exhausted tray icon shows the time until the quota opens
/// again as short text ("1d", "9h", "45m") in the middle of the ring -- the formatter, which window it
/// counts to, the pixel font's fit inside the ring, its contrast, and how often the icon is re-rendered.
/// </summary>
public class CountdownIconTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly Color DarkTaskbar = Color.FromArgb(255, 0x20, 0x20, 0x20);

    // ---- the formatter ----

    [Theory]
    [InlineData(-5 * 60, "1m")]      // already past: never "0m"
    [InlineData(0, "1m")]
    [InlineData(1, "1m")]
    [InlineData(59, "1m")]           // 59 s left
    [InlineData(60, "1m")]
    [InlineData(61, "2m")]
    [InlineData(44 * 60 + 30, "45m")]
    [InlineData(59 * 60, "59m")]
    [InlineData(59 * 60 + 1, "1h")]  // rounds up to 60 min: the next unit, not "60m"
    [InlineData(3600, "1h")]
    [InlineData(3601, "2h")]         // 1 h 0 min 1 s
    [InlineData(3600 + 60, "2h")]    // 1 h 1 min
    [InlineData(8 * 3600 + 1800, "9h")]
    [InlineData(23 * 3600, "23h")]
    [InlineData(23 * 3600 + 1, "1d")] // rounds up to 24 h: "1d", not "24h"
    [InlineData(24 * 3600, "1d")]
    [InlineData(24 * 3600 + 1, "2d")]
    [InlineData(5 * 86400 + 12 * 3600, "6d")]
    [InlineData(7 * 86400, "7d")]
    [InlineData(400 * 86400, "99d")] // capped: three characters
    public void Format_UsesTheLargestWholeUnit_RoundedUp(long seconds, string expected) =>
        Assert.Equal(expected, CountdownText.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Format_RoundsASubSecondRemainderUp()
    {
        Assert.Equal("2m", CountdownText.Format(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)));
        Assert.Equal("1m", CountdownText.Format(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Format_IsADuration_SoADaylightSavingChangeInBetweenCountsItsRealHours()
    {
        // Europe/Stockholm, the night the clocks go back: 24 h on the wall clock is 25 real hours.
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2));
        var until = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(25), until - now);
        Assert.Equal("2d", CountdownText.Format(until - now));
    }

    [Fact]
    public void Format_NeverExceedsThreeCharacters_NeverSaysZero_AndEveryTokenCanBeDrawn()
    {
        string? previous = null;
        for (int minutes = 0; minutes <= 9 * 24 * 60; minutes++)
        {
            string text = CountdownText.Format(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(7));
            Assert.InRange(text.Length, 2, 3);
            Assert.False(text.StartsWith('0'), text);
            Assert.True(CountdownGlyphs.CanDraw(text), text);
            previous = text;
        }
        Assert.NotNull(previous);
    }

    // ---- which time ----

    static WindowView Window(WindowKind kind, DateTimeOffset resetsAt, bool spent) => new(
        kind, kind == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes, spent ? 100 : 30, resetsAt,
        spent ? QuotaState.Spent : QuotaState.Safe, 0.1, 1.0, spent ? 100 : 40, null, TimeSpan.Zero, null);

    /// <summary>A view as the model builds it: BlockedUntil is the latest reset among the spent windows.</summary>
    static QuotaView View(DateTimeOffset sessionReset, bool sessionSpent, DateTimeOffset weeklyReset, bool weeklySpent)
    {
        DateTimeOffset? blocked = null;
        if (sessionSpent) blocked = sessionReset;
        if (weeklySpent && (blocked is null || weeklyReset > blocked)) blocked = weeklyReset;
        return new QuotaView(Window(WindowKind.Session, sessionReset, sessionSpent), Window(WindowKind.Weekly, weeklyReset, weeklySpent),
            Freshness.Live, Now, Now, TimeSpan.FromSeconds(300), null, QuotaState.Spent, blocked);
    }

    static string? TextFor(QuotaView view, int px = 16, bool dark = true) => QuotaIconParams.Build(view, px, dark, Now).Countdown;

    [Fact]
    public void WeeklySpent_CountsToTheWeeklyReset()
    {
        QuotaView view = View(Now.AddHours(2), sessionSpent: false, Now.AddDays(4.5), weeklySpent: true);

        Assert.Equal("5d", TextFor(view));
    }

    [Fact]
    public void OnlySessionSpent_CountsToTheSessionReset_NotTheWeekly()
    {
        QuotaView view = View(Now.AddMinutes(44.5), sessionSpent: true, Now.AddDays(4.5), weeklySpent: false);

        Assert.Equal("45m", TextFor(view));
    }

    [Fact]
    public void BothSpent_CountsToTheLaterReset()
    {
        QuotaView view = View(Now.AddHours(2), sessionSpent: true, Now.AddHours(22.5), weeklySpent: true);

        Assert.Equal("23h", TextFor(view));
    }

    [Fact]
    public void ANotExhaustedIcon_HasNoCountdown()
    {
        QuotaView open = View(Now.AddHours(2), sessionSpent: false, Now.AddDays(2), weeklySpent: false) with { BlockedUntil = null };

        Assert.Null(TextFor(open));
        Assert.False(QuotaIconParams.Build(open, 16, true, Now).Exhausted);
    }

    // ---- the pixel font: size, fit, centring ----

    static IEnumerable<string> EveryToken()
    {
        for (int d = 1; d <= 99; d++) yield return $"{d}d";
        for (int h = 1; h <= 23; h++) yield return $"{h}h";
        for (int m = 1; m <= 59; m++) yield return $"{m}m";
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    public void EveryToken_IsCentredOnTheWholeIcon_InsideIt_AndSevenCellsTall(int px)
    {
        int k = Math.Max(1, px / 16);
        double centre = px / 2.0;

        foreach (string token in EveryToken())
        {
            CountdownGlyphs.Layout layout = CountdownGlyphs.For(token, px);
            Assert.Equal(7 * k, layout.Bounds.Height);                          // one small set for every token
            Assert.InRange(layout.Bounds.Width, 1, px);                           // never wider than the icon
            Assert.InRange(Math.Abs(layout.Bounds.Left + layout.Bounds.Width / 2.0 - centre), 0, 0.5 * k);   // centred (half a cell at most)
            Assert.InRange(Math.Abs(layout.Bounds.Top + layout.Bounds.Height / 2.0 - centre), 0, 0.5 * k);
            foreach (Rectangle r in layout.Ink)
                Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= px && r.Bottom <= px, $"{token} @{px}: ink outside the icon");
        }
    }

    [Fact]
    public void AtSixteenPixels_EveryTokenIsSevenPixelsTall_ThreeCharactersFillTheWidth_TwoSitInsideTheRing()
    {
        Assert.All(EveryToken(), t => Assert.Equal(7, CountdownGlyphs.For(t, 16).Bounds.Height));

        int widest3 = EveryToken().Where(t => t.Length == 3).Max(t => CountdownGlyphs.For(t, 16).Bounds.Width);
        int widest2 = EveryToken().Where(t => t.Length == 2).Max(t => CountdownGlyphs.For(t, 16).Bounds.Width);
        Assert.Equal(15, widest3);                                                // "45m" and friends: 4 + 1 + 4 + 1 + 5, one pixel from edge to edge
        Assert.Equal(10, widest2);                                                // "9m": 4 + 1 + 5
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TwoCharacterTokens_SitInsideTheRingsClearInterior(int px)
    {
        // the ring's inner edge (GaugeRenderer.RingGeometry: band 15.5 % of the icon, outer edge 2 % in); the square
        // corner of a glyph (the top of a "5", the bottom of a "2") may graze it by up to half a cell, never more
        double innerRadius = px * (0.5 - 0.02 - 0.155);
        double centre = px / 2.0;

        foreach (string token in EveryToken().Where(t => t.Length == 2))
        {
            bool[,] ink = CountdownGlyphs.InkMask(CountdownGlyphs.For(token, px), px);
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    if (ink[x, y])
                    {
                        double dist = Math.Sqrt((x + 0.5 - centre) * (x + 0.5 - centre) + (y + 0.5 - centre) * (y + 0.5 - centre));
                        Assert.True(dist <= innerRadius + 0.6 * Math.Max(1, px / 16), $"{token} @{px}: ink pixel ({x},{y}) at {dist:F2} px is over the ring (inner edge {innerRadius:F2})");
                    }
        }
    }

    [Fact]
    public void EveryGlyph_IsDistinct()
    {
        var seen = new Dictionary<string, char>();
        foreach (char c in "0123456789dhm")
        {
            string key = string.Join("/", CountdownGlyphs.ShapeOf(c));
            Assert.False(seen.TryGetValue(key, out char other), $"'{c}' draws the same as '{other}'");
            seen[key] = c;
        }
    }
    // ---- what is drawn: crisp, undimmed, contrasting, over a quiet ring ----

    static TimeSpan Left(string token) => token switch
    {
        "23h" => TimeSpan.FromHours(22.5),
        "45m" => TimeSpan.FromMinutes(44.5),
        "1d" => TimeSpan.FromDays(1),
        "3d" => TimeSpan.FromDays(2.5),
        "10m" => TimeSpan.FromMinutes(9.5),
        "1m" => TimeSpan.FromSeconds(30),
        "9h" => TimeSpan.FromHours(8.5),
        _ => TimeSpan.FromMinutes(58.5),
    };

    public static IEnumerable<object[]> TextCases()
    {
        foreach (string token in new[] { "3d", "9h", "1m", "1d", "23h", "45m", "10m", "59m" })
            foreach (int px in new[] { 16, 20, 24, 32 })
                foreach (bool dark in new[] { true, false })
                    yield return new object[] { token, px, dark };
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void TheText_IsOpaque_PixelExact_AndMeetsFourPointFiveToOne_AgainstWhatIsUnderIt(string token, int px, bool dark)
    {
        QuotaView view = View(Now.AddHours(2), false, Now + Left(token), true);
        QuotaIconParams p = QuotaIconParams.Build(view, px, dark, Now);
        Assert.Equal(token, p.Countdown);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        using Bitmap ringOnly = GaugeRenderer.RenderQuota(p with { Countdown = null });   // what the text lies over
        Color background = dark ? DarkTaskbar : Palette.LightTaskbar;
        Color expected = GaugeRenderer.CountdownTextColour(dark);
        CountdownGlyphs.Layout layout = CountdownGlyphs.For(token, px);
        bool[,] ink = CountdownGlyphs.InkMask(layout, px);

        int inkPixels = 0, overRing = 0;
        double worst = double.MaxValue;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                if (ink[x, y])
                {
                    inkPixels++;
                    Assert.Equal(expected, icon.GetPixel(x, y));                        // opaque, the exact text colour: not dimmed, not blended
                    Color under = ringOnly.GetPixel(x, y);
                    if (under.A > 0) overRing++;
                    worst = Math.Min(worst, Contrast(expected, under, background));
                }
            }

        Assert.True(inkPixels > 20);
        Assert.True(worst >= 4.5, $"{token} @{px} {(dark ? "dark" : "light")}: text contrast {worst:F2}:1 against what is under it, need >= 4.5:1");
        if (px == 16) Assert.True(overRing > 0, "at 16 px the text lies over the ring (that is the point)");
    }

    /// <summary>WCAG contrast of an opaque text colour against `under` (a possibly translucent icon pixel) composited on the taskbar.</summary>
    static double Contrast(Color text, Color under, Color taskbar) =>
        Palette.Contrast(text, 1.0, under.A == 0 ? taskbar : Blend(under, taskbar));

    static Color Blend(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        return Color.FromArgb((int)Math.Round(fg.R * a + bg.R * (1 - a)), (int)Math.Round(fg.G * a + bg.G * (1 - a)), (int)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    public static IEnumerable<object[]> RingCases()
    {
        foreach (int px in new[] { 16, 20, 24, 32 })
            foreach (bool dark in new[] { true, false })
                yield return new object[] { px, dark };
    }

    [Theory]
    [MemberData(nameof(RingCases))]
    public void TheRing_IsADimBackdrop_ButStillReadsAsARedRing(int px, bool dark)
    {
        QuotaIconParams p = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddHours(22.5), true), px, dark, Now);
        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        Color background = dark ? DarkTaskbar : Palette.LightTaskbar;
        bool[,] ink = CountdownGlyphs.InkMask(CountdownGlyphs.For(p.Countdown!, px), px);

        // core = the pixels at the ring's full (dimmed) alpha, text left out
        int ceiling = 0;
        for (int y = 0; y < px; y++) for (int x = 0; x < px; x++) if (!ink[x, y]) ceiling = Math.Max(ceiling, icon.GetPixel(x, y).A);
        var contrasts = new List<double>();
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                Color c = icon.GetPixel(x, y);
                if (ink[x, y] || c.A < 0.95 * ceiling) continue;
                Assert.True(c.R > c.G && c.R > c.B, "the ring is red");                       // hue kept on both taskbars
                contrasts.Add(Palette.Contrast(c, c.A / 255.0, background));
            }
        contrasts.Sort();
        Assert.True(contrasts.Count > 4);
        double median = contrasts[contrasts.Count / 2];

        Assert.Equal((int)Math.Round(255 * GaugeRenderer.ExhaustedDimAlpha), ceiling);        // dimmed to the backdrop level
        Assert.True(median >= 1.5, $"ring core {median:F2}:1 on {(dark ? "dark" : "light")} @{px}: too faint to read as a ring (floor 1.5:1)");
        Assert.True(median < 3.0, $"ring core {median:F2}:1: no longer a backdrop");
    }

    [Fact]
    public void TheText_IsBrighterThanTheDimmedRing_SoItReadsFirst()
    {
        QuotaIconParams p = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddHours(22.5), true), 16, true, Now);
        using Bitmap icon = GaugeRenderer.RenderQuota(p);

        Rectangle ink = CountdownGlyphs.For(p.Countdown!, 16).Ink[0];
        Color text = icon.GetPixel(ink.X, ink.Y);
        Color ring = icon.GetPixel(8, 1);   // top of the ring

        Assert.True(Palette.Contrast(text, 1.0, DarkTaskbar) > Palette.Contrast(Color.FromArgb(255, ring), ring.A / 255.0, DarkTaskbar));
    }

    [Fact]
    public void TheRing_KeepsItsFullOuterDiameter_AndIsStillThere_BehindTheText()
    {
        QuotaIconParams withText = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddHours(22.5), true), 16, true, Now);
        using Bitmap a = GaugeRenderer.RenderQuota(withText);
        using Bitmap b = GaugeRenderer.RenderQuota(withText with { Countdown = null });

        // the ring's topmost and bottommost rows are untouched by the text, and the ring is the normal band
        for (int x = 4; x < 12; x++)
        {
            Assert.Equal(b.GetPixel(x, 0), a.GetPixel(x, 0));
            Assert.Equal(b.GetPixel(x, 15), a.GetPixel(x, 15));
        }
        Assert.True(b.GetPixel(8, 0).A > 0 && b.GetPixel(8, 2).A > 0, "the ring's band is the normal two to three pixels");
        Assert.Equal(0, withText.PieFrac);
    }

    [Fact]
    public void TheCountdownPie_IsGone_AndTheTextIsPartOfTheChangeFingerprint()
    {
        // Same icon, text "1m" vs "23h": the only difference between them lies inside the ring.
        QuotaIconParams a = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddSeconds(30), true), 16, true, Now);
        QuotaIconParams b = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddHours(22.5), true), 16, true, Now);
        Assert.Equal(0, a.PieFrac);
        Assert.Equal(0, b.PieFrac);
        Assert.NotEqual(a, b);
    }

    // ---- when the icon is re-rendered ----

    [Fact]
    public void Ticking_OncePerSecond_RendersExactlyWhenTheTextChanges_AndTheTextIsNeverStale()
    {
        // Ten minutes and a half to go; one tick per second for twelve minutes: the text is "11m", "10m", ..., then "1m" (and stays there once past).
        DateTimeOffset until = Now.AddMinutes(10).AddSeconds(30);
        QuotaView view = View(Now.AddHours(2), false, until, true);
        QuotaIconParams? shown = null;
        long lastRender = long.MinValue;
        var rendered = new List<string?>();

        for (int second = 0; second <= 12 * 60; second++)
        {
            long mono = 1_000_000 + second * 1000L;
            QuotaIconParams p = QuotaIconParams.Build(view, 16, true, Now.AddSeconds(second));
            if (IconSlot.ShouldRender(shown, p, mono, lastRender))
            {
                shown = p;
                lastRender = mono;
                rendered.Add(p.Countdown);
            }
            Assert.Equal(p.Countdown, shown!.Value.Countdown);      // what is on screen is what the clock says, every second
        }

        Assert.Equal(new[] { "11m", "10m", "9m", "8m", "7m", "6m", "5m", "4m", "3m", "2m", "1m" }, rendered);
    }

    [Fact]
    public void AnExhaustedChange_IsHeldBackBriefly_ButNeverLost()
    {
        QuotaView first = View(Now.AddHours(2), false, Now.AddMinutes(30.5), true);
        QuotaIconParams a = QuotaIconParams.Build(first, 16, true, Now);
        QuotaIconParams b = QuotaIconParams.Build(first, 16, true, Now.AddMinutes(1));
        Assert.NotEqual(a, b);

        long t = 5_000_000;
        Assert.False(IconSlot.ShouldRender(a, b, t + 5_000, t));                          // 5 s after the last render: held back
        Assert.True(IconSlot.ShouldRender(a, b, t + 15_000, t));                          // the guard has passed: through
        Assert.False(IconSlot.ShouldRender(b, b, t + 600_000, t));                        // nothing changed: never
    }

    [Fact]
    public void ASizeOrThemeChange_AlwaysRenders_AndSoDoesTheFirstIcon_AndNonExhaustedChanges()
    {
        QuotaView view = View(Now.AddHours(2), false, Now.AddMinutes(30.5), true);
        QuotaIconParams dark16 = QuotaIconParams.Build(view, 16, true, Now);
        QuotaIconParams light16 = QuotaIconParams.Build(view, 16, false, Now);
        QuotaIconParams dark20 = QuotaIconParams.Build(view, 20, true, Now);
        long t = 5_000_000;

        Assert.True(IconSlot.ShouldRender(null, dark16, t, long.MinValue));
        Assert.True(IconSlot.ShouldRender(dark16, light16, t + 1, t));
        Assert.True(IconSlot.ShouldRender(dark16, dark20, t + 1, t));

        QuotaIconParams safe = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddDays(2), false) with { BlockedUntil = null }, 16, true, Now);
        QuotaIconParams safeMore = safe with { RingFrac = safe.RingFrac + 0.1 };
        Assert.True(IconSlot.ShouldRender(safe, safeMore, t + 1, t));                      // the throttle is for the exhausted icon only
        Assert.False(IconSlot.ShouldRender(safe, safe, t + 1_000_000, t));
    }
}

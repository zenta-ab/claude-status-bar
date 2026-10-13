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

    // ---- the pixel font fits the ring ----

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
    public void EveryToken_FitsInsideTheRing_AndNeverReachesItsOuterEdge(int px)
    {
        var style = CountdownGlyphs.StyleFor(px);
        double centre = px / 2.0;
        double band = px * style.RingBandFraction;
        double centreLine = px * 0.5 - band * 0.5 - px * 0.02;   // GaugeRenderer.RingGeometry's rOuter
        double innerEdge = centreLine - band / 2;
        double outerEdge = centreLine + band / 2;

        foreach (string token in EveryToken())
        {
            CountdownGlyphs.Layout layout = CountdownGlyphs.For(token, px);
            Assert.InRange(Math.Abs(layout.Bounds.Left + layout.Bounds.Width / 2.0 - centre), 0, 0.5);   // centred (half a pixel at most)
            Assert.InRange(Math.Abs(layout.Bounds.Top + layout.Bounds.Height / 2.0 - centre), 0, 0.5);

            foreach (Rectangle r in layout.Ink)
            {
                Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= px && r.Bottom <= px, $"{token} @{px}: ink outside the icon");
                foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Left, r.Bottom), (r.Right, r.Bottom) })
                {
                    double dist = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                    // the farthest corner of the corner pixels may dip into the ring's inner edge, a pixel at most, and stays clear of its outer edge
                    Assert.True(dist <= outerEdge - 0.75, $"{token} @{px}: ink reaches {dist:F2} px, too close to the ring's outer edge {outerEdge:F2}");
                    Assert.True(dist <= innerEdge + 1.1, $"{token} @{px}: ink overlaps the ring's inner edge ({innerEdge:F2}) by more than a pixel ({dist:F2})");
                }
            }
        }
    }

    [Fact]
    public void TheSixteenPixelGlyphs_AreWholePixels_ThreeWide_FiveTall()
    {
        foreach (char c in "0123456789dhm")
        {
            CountdownGlyphs.Layout layout = CountdownGlyphs.For(c.ToString(), 16);
            Assert.Equal(5, layout.Bounds.Height);
            Assert.InRange(layout.Bounds.Width, 2, 3);
            Assert.All(layout.Ink, r => Assert.Equal(new Size(1, 1), r.Size));
        }
        Assert.Equal(11, CountdownGlyphs.For("23h", 16).Bounds.Width);   // the widest token: three characters and two gaps
    }

    [Fact]
    public void EveryGlyph_IsDistinct()
    {
        var seen = new Dictionary<string, char>();
        foreach (char c in "0123456789dhm")
        {
            string key = string.Join(";", CountdownGlyphs.For(c.ToString(), 16).Ink.Select(r => $"{r.X - CountdownGlyphs.For(c.ToString(), 16).Bounds.X},{r.Y}"));
            Assert.False(seen.TryGetValue(key, out char other), $"'{c}' draws the same as '{other}'");
            seen[key] = c;
        }
    }

    // ---- what is drawn: crisp, undimmed, contrasting ----

    [Theory]
    [InlineData("23h", 16, true)]
    [InlineData("45m", 16, true)]
    [InlineData("23h", 16, false)]
    [InlineData("45m", 16, false)]
    [InlineData("1d", 20, true)]
    [InlineData("10m", 24, false)]
    [InlineData("59m", 32, true)]
    [InlineData("59m", 32, false)]
    public void TheText_IsOpaque_PixelExact_AndMeetsThreeToOneOnItsTaskbar(string token, int px, bool dark)
    {
        QuotaView view = View(Now.AddHours(2), false, Now + (token switch { "23h" => TimeSpan.FromHours(22.5), "45m" => TimeSpan.FromMinutes(44.5), "1d" => TimeSpan.FromDays(1), "10m" => TimeSpan.FromMinutes(9.5), _ => TimeSpan.FromMinutes(58.5) }), true);
        QuotaIconParams p = QuotaIconParams.Build(view, px, dark, Now);
        Assert.Equal(token, p.Countdown);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        Color background = dark ? DarkTaskbar : Palette.LightTaskbar;
        Color expected = GaugeRenderer.CountdownTextColour(dark);

        var contrasts = new List<double>();
        foreach (Rectangle r in CountdownGlyphs.For(token, px).Ink)
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    Color c = icon.GetPixel(x, y);
                    Assert.Equal(expected, c);                      // opaque, the exact text colour: not dimmed, not blended
                    contrasts.Add(Palette.Contrast(c, 1.0, background));
                }
        contrasts.Sort();
        Assert.True(contrasts.Count > 8);
        Assert.True(contrasts[contrasts.Count / 2] >= 3.0, $"{token} @{px} {(dark ? "dark" : "light")}: text contrast {contrasts[contrasts.Count / 2]:F2}:1, need >= 3:1");
        Assert.True(contrasts[0] >= 3.0);
    }

    [Fact]
    public void TheText_IsBrighterThanTheDimmedRing_SoItReadsFirst()
    {
        QuotaIconParams p = QuotaIconParams.Build(View(Now.AddHours(2), false, Now.AddHours(22.5), true), 16, true, Now);
        using Bitmap icon = GaugeRenderer.RenderQuota(p);

        Rectangle ink = CountdownGlyphs.For(p.Countdown!, 16).Ink[0];
        Color text = icon.GetPixel(ink.X, ink.Y);
        Color ring = icon.GetPixel(0 + 8, 1);   // top of the ring

        Assert.True(Palette.Contrast(text, 1.0, DarkTaskbar) > Palette.Contrast(Color.FromArgb(255, ring), ring.A / 255.0, DarkTaskbar));
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

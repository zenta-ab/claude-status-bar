using System.Drawing;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/forecast-and-states.md, "Exhausted": the solid ring / countdown pie must measure
/// >= 3:1 contrast against a dark taskbar (#202020) at 16px -- the earlier #6C7480 at 42%
/// alpha measured only 1.71:1. 3:1 (not 5:1) is the deliberate target here: this state must
/// read as quieter/duller than an active one, not merely legible, and real critical red
/// tops out around 3.8:1 fully opaque, so a stricter bar would force a lightened, brighter
/// tint -- exactly the "brightest glyph of them all" regression this is guarding against
/// (see ExhaustedIcon_At16px_IsQuieterThanActiveStates below). This is the actual pixel
/// measurement the doc asks for, not hand math: it renders the real icon and blends every
/// solid glyph pixel against #202020 using the WCAG contrast formula. Also covers
/// Freshness.Unknown's exclamation mark (must draw ink in the centre, unlike the old
/// empty-ring glyph).
/// </summary>
public class IconContrastTests
{
    static readonly Color DarkTaskbar = Color.FromArgb(255, 0x20, 0x20, 0x20);

    static readonly DateTimeOffset UtcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static QuotaView SpentView()
    {
        var session = new WindowView(
            WindowKind.Session, QuotaWindows.SessionMinutes, 99.7, UtcNow.AddMinutes(180), QuotaState.Spent,
            RatePctPerMin: 0.4, PaceMultiple: 1.2, ProjectedPctAtReset: 100, DepletesAt: null,
            Shortfall: TimeSpan.Zero, MeasuringReason: null);
        WindowView weekly = WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes);

        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: UtcNow, LastSuccessAt: UtcNow, PollInterval: TimeSpan.FromSeconds(300),
            Error: null, IconSeverity: QuotaState.Spent, BlockedUntil: session.ResetsAt);
    }

    /// <summary>
    /// Every glyph is antialiased, so its edge pixels have every alpha from 0 to full and no cutoff means
    /// anything on them. What a person sees as "the colour" is the glyph's CORE: the pixels whose coverage
    /// is at least 95 % (alpha at least 0.95 of the glyph's own ceiling -- 255, or the exhausted glyph's
    /// dim level). The 3:1 requirement is on the MEDIAN contrast of that core against the taskbar, on a
    /// dark AND a light taskbar, at every tray size. (The stale, awaiting-reset and empty "measuring"
    /// glyphs are quiet by design, and their core is a 1-4 pixel dot or tick, so they are not measured.)
    /// </summary>
    public static IEnumerable<object[]> CoreContrastCases()
    {
        foreach (string state in new[] { "safe", "tight", "dry_early", "dry_early_weekly", "spent_session", "spent_weekly", "unknown", "loading", "not_logged_in" })
            foreach (int px in new[] { 16, 20, 24, 32 })
                foreach (bool dark in new[] { true, false })
                    yield return new object[] { state, px, dark };
    }

    [Theory]
    [MemberData(nameof(CoreContrastCases))]
    public void GlyphCore_MeetsThreeToOne_OnDarkAndLightTaskbars_AtEveryTraySize(string state, int px, bool dark)
    {
        QuotaView view = DemoQuotaSource.Build(UtcNow).First(s => s.Key == state).View;
        QuotaIconParams p = QuotaIconParams.Build(view, px, dark, UtcNow);
        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        Color background = dark ? DarkTaskbar : LightTaskbar;

        (double median, int count) = MedianCoreContrast(icon, background);

        Assert.True(count > 0, $"{state} {px}px: no core pixels (coverage >= 95%) to measure");
        Assert.True(median >= 3.0, $"{state} {px}px on {(dark ? "dark" : "light")}: core contrast {median:F2}:1 over {count} px, need >= 3:1");
    }

    /// <summary>
    /// The antialiasing regression: the spent glyph used to be drawn with antialiasing OFF, so its ring and
    /// pie were pure stair-steps (every pixel fully on or fully off). Every glyph with a curve must have a
    /// graded edge: a healthy number of pixels strictly between transparent and its ceiling.
    /// </summary>
    [Theory]
    [InlineData("spent_session")]
    [InlineData("spent_weekly")]
    [InlineData("safe")]
    [InlineData("dry_early")]
    [InlineData("unknown")]
    [InlineData("not_logged_in")]
    [InlineData("loading")]
    [InlineData("stale")]
    public void EveryGlyph_HasAGradedEdge_AtEveryTraySize(string state)
    {
        foreach (int px in new[] { 16, 20, 24, 32 })
        {
            QuotaView view = DemoQuotaSource.Build(UtcNow).First(s => s.Key == state).View;
            using Bitmap icon = GaugeRenderer.RenderQuota(QuotaIconParams.Build(view, px, taskbarDark: true, UtcNow));
            int maxA = MaxAlpha(icon);
            int partial = 0, distinct;
            var levels = new HashSet<int>();
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    int a = icon.GetPixel(x, y).A;
                    if (a > 24 && a < 0.9 * maxA) partial++;
                    if (a > 0) levels.Add(a / 16);
                }
            distinct = levels.Count;

            Assert.True(partial >= px, $"{state} {px}px has only {partial} partially covered pixels: its edges are stair-stepped");
            Assert.True(distinct >= 5, $"{state} {px}px uses only {distinct} alpha levels");
        }
    }

    static int MaxAlpha(Bitmap icon)
    {
        int max = 0;
        for (int y = 0; y < icon.Height; y++)
            for (int x = 0; x < icon.Width; x++) max = Math.Max(max, icon.GetPixel(x, y).A);
        return max;
    }

    /// <summary>Median contrast against `background` of the pixels with at least 95 % of the glyph's own maximum alpha (the "core").</summary>
    static (double Median, int Count) MedianCoreContrast(Bitmap icon, Color background)
    {
        int ceiling = MaxAlpha(icon);
        var contrasts = new List<double>();
        for (int y = 0; y < icon.Height; y++)
            for (int x = 0; x < icon.Width; x++)
            {
                Color px = icon.GetPixel(x, y);
                if (px.A < 0.95 * ceiling) continue;
                contrasts.Add(ContrastRatio(RelativeLuminance(Blend(px, background)), RelativeLuminance(background)));
            }
        if (contrasts.Count == 0) return (0, 0);
        contrasts.Sort();
        return (contrasts[contrasts.Count / 2], contrasts.Count);
    }

    static readonly Color LightTaskbar = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

    /// <summary>
    /// The user's actual requirement, not just a contrast floor: "red and dimmed, but full" --
    /// the exhausted glyph must visibly read as QUIETER than the active Tight/Safe glyphs, not
    /// merely above the 3:1 legibility floor. A previous round cleared 5:1 by lightening the red
    /// to a pink-ish tint (Palette.CritDimmed, #FF7A63) at full strength, which made it the
    /// BRIGHTEST glyph on the whole contact sheet -- the opposite of "quieter". This pins the
    /// relative-brightness relationship directly (via the same contrast-against-background
    /// measure the 3:1 test uses) so a future contrast-only fix can't silently regress the
    /// "reads as disabled" intent again.
    /// </summary>
    [Fact]
    public void ExhaustedIcon_At16px_IsQuieterThanActiveStates()
    {
        QuotaIconParams spentParams = QuotaIconParams.Build(SpentView(), px: 16, taskbarDark: true, UtcNow.AddMinutes(60));
        QuotaIconParams tightParams = QuotaIconParams.Build(UniformView(QuotaState.Tight, 50.0), px: 16, taskbarDark: true, UtcNow);
        QuotaIconParams safeParams = QuotaIconParams.Build(UniformView(QuotaState.Safe, 50.0), px: 16, taskbarDark: true, UtcNow);

        using Bitmap spent = GaugeRenderer.RenderQuota(spentParams);
        using Bitmap tight = GaugeRenderer.RenderQuota(tightParams);
        using Bitmap safe = GaugeRenderer.RenderQuota(safeParams);

        double spentContrast = MedianCoreContrast(spent, DarkTaskbar).Median;
        double tightContrast = MedianCoreContrast(tight, DarkTaskbar).Median;
        double safeContrast = MedianCoreContrast(safe, DarkTaskbar).Median;

        Assert.True(spentContrast < tightContrast,
            $"exhausted icon (measured {spentContrast:F2}:1) must read as quieter than the Tight icon " +
            $"(measured {tightContrast:F2}:1) -- if this fails, someone brightened the spent colour again");
        Assert.True(spentContrast < safeContrast,
            $"exhausted icon (measured {spentContrast:F2}:1) must read as quieter than the Safe icon " +
            $"(measured {safeContrast:F2}:1) -- if this fails, someone brightened the spent colour again");
    }

    /// <summary>Weekly and session both pinned to the same state/percentage, so the whole glyph (ring + pie) renders in one colour -- a clean sample for the relative-brightness check above.</summary>
    static QuotaView UniformView(QuotaState state, double usedPct)
    {
        var session = new WindowView(
            WindowKind.Session, QuotaWindows.SessionMinutes, usedPct, UtcNow.AddMinutes(180), state,
            RatePctPerMin: 0.1, PaceMultiple: 1.0, ProjectedPctAtReset: usedPct, DepletesAt: null,
            Shortfall: TimeSpan.Zero, MeasuringReason: null);
        var weekly = new WindowView(
            WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct, UtcNow.AddDays(3), state,
            RatePctPerMin: 0.01, PaceMultiple: 1.0, ProjectedPctAtReset: usedPct, DepletesAt: null,
            Shortfall: TimeSpan.Zero, MeasuringReason: null);
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: UtcNow, LastSuccessAt: UtcNow, PollInterval: TimeSpan.FromSeconds(300),
            Error: null, IconSeverity: state, BlockedUntil: null);
    }

    /// <summary>
    /// The user's own complaint: the earlier dashed ring "reads like a lifebuoy". Verifies
    /// the ring itself (not the pie, which is deliberately excluded by the inner-annulus cut)
    /// has ink at every angle around its circumference -- a dash pattern of {2.2, 2.0} at
    /// 16px would leave gaps far wider than one 15-degree bucket, so any empty bucket here
    /// means the ring regressed back to a dashed look.
    /// </summary>
    [Fact]
    public void ExhaustedIcon_At16px_RingHasNoGapsAroundItsCircumference()
    {
        // pieFrac only changes the inner pie's ANGULAR sweep, never its radius, so the
        // radius-based inner-annulus cut in AssertRingHasNoGaps excludes the whole pie disc
        // regardless of how filled it is -- any point in the countdown works here.
        QuotaIconParams p = QuotaIconParams.Build(SpentView(), px: 16, taskbarDark: true, UtcNow.AddMinutes(60));
        Assert.True(p.Exhausted);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        AssertRingHasNoGaps(icon);
    }

    /// <summary>
    /// docs/forecast-and-states.md "Freshness ladder", Unknown: the glyph must draw ink in
    /// the centre (the exclamation mark) so it reads as "can't read the quota", not "still
    /// loading" -- the old glyph was an empty ring, indistinguishable from a genuinely
    /// 0%-used icon between polls.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void UnknownIcon_AtEverySize_HasInkInTheCentre(int px)
    {
        QuotaIconParams p = QuotaIconParams.Build(QuotaView.Initial, px, taskbarDark: true, UtcNow);
        Assert.True(p.Outline);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        Assert.True(HasInkNearCentre(icon),
            $"expected the Unknown glyph to draw ink (the exclamation mark) in the centre region at {px}px, not an empty ring");
    }

    // ---- the padlock (zero accounts, NeedsLogin) ----

    static QuotaView NeedsLoginView() => QuotaView.Initial with { NeedsLogin = true };

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void PadlockIcon_AtEverySize_DrawsABodyAndAShackleInsideTheRing(int px)
    {
        QuotaIconParams p = QuotaIconParams.Build(NeedsLoginView(), px, taskbarDark: true, UtcNow);
        Assert.True(p.Padlock);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        GaugeRenderer.PadlockRects r = GaugeRenderer.PadlockLayout(px);

        // Ink where the body is (outside its keyhole) and in every shackle bar.
        int bodyInk = 0, bodyPixels = 0;
        for (int y = r.Body.Top; y < r.Body.Bottom; y++)
            for (int x = r.Body.Left; x < r.Body.Right; x++)
            {
                if (r.Keyhole is { } k && k.Contains(x, y)) continue;
                bodyPixels++;
                if (icon.GetPixel(x, y).A >= 200) bodyInk++;
            }
        Assert.True(bodyInk >= bodyPixels - 4, $"padlock body is hollow at {px}px ({bodyInk}/{bodyPixels} inked)"); // up to the four antialiased corner pixels
        foreach (Rectangle bar in r.Shackle)
            Assert.True(icon.GetPixel(bar.X + bar.Width / 2, bar.Y + bar.Height / 2).A >= 120, $"a shackle bar is empty at {px}px"); // the top bar is an arc: antialiased, never empty

        // Everything stays inside the ring's inner edge and clear of its band.
        double innerRadius = px * 0.5 - px * 0.02 - px * 0.155; // inner edge of the ring stroke
        double cx = px / 2.0, cy = px / 2.0;
        foreach (Rectangle rect in r.Shackle.Append(r.Body))
            foreach (Point corner in new[] { new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top), new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom) })
                Assert.True(Math.Sqrt((corner.X - cx) * (corner.X - cx) + (corner.Y - cy) * (corner.Y - cy)) <= innerRadius + 0.5,
                    $"the padlock touches the ring at {px}px (corner {corner})");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void PadlockIcon_OnDarkTaskbar_MeetsMinimumContrast_AndHasInkInTheCentre(int px)
    {
        QuotaIconParams p = QuotaIconParams.Build(NeedsLoginView(), px, taskbarDark: true, UtcNow);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        Assert.True(HasInkNearCentre(icon), $"no ink in the centre at {px}px");
        GaugeRenderer.PadlockRects r = GaugeRenderer.PadlockLayout(px);
        Color body = icon.GetPixel(r.Body.Left + 1, r.Body.Bottom - 2); // inside the body, clear of any keyhole
        Assert.True(ContrastOn(body) >= 3.0, $"padlock body contrast {ContrastOn(body):F2}:1 at {px}px, need >= 3:1");
    }

    [Fact]
    public void PadlockIcon_DiffersFromTheExclamationMark_AtEverySize()
    {
        foreach (int px in new[] { 16, 20, 24, 32 })
        {
            using Bitmap padlock = GaugeRenderer.RenderQuota(QuotaIconParams.Build(NeedsLoginView(), px, true, UtcNow));
            using Bitmap unknown = GaugeRenderer.RenderQuota(QuotaIconParams.Build(QuotaView.Initial, px, true, UtcNow));
            bool differs = false;
            for (int y = 0; y < px && !differs; y++)
                for (int x = 0; x < px && !differs; x++)
                    differs = padlock.GetPixel(x, y) != unknown.GetPixel(x, y);
            Assert.True(differs, $"padlock and \"!\" are identical at {px}px");
        }
    }

    static double ContrastOn(Color c) => ContrastRatio(RelativeLuminance(Blend(c, DarkTaskbar)), RelativeLuminance(DarkTaskbar));

    static bool HasInkNearCentre(Bitmap icon)
    {
        int w = icon.Width, h = icon.Height;
        double cx = w / 2.0, cy = h / 2.0;
        double innerRadius = w * 0.30; // well inside the ring's own band -- always transparent on the old empty-ring glyph
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                if (Math.Sqrt(dx * dx + dy * dy) > innerRadius) continue;
                if (icon.GetPixel(x, y).A > 40) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Buckets every near-opaque pixel in the outer annulus (the ring's territory -- the
    /// inner 30% radius, the pie/erased gap, is excluded) by angle around the centre and
    /// requires every bucket to have at least one hit, i.e. no gap wide enough to read as a
    /// dash. Generic over the actual ring geometry (band width, radius) rather than
    /// duplicating GaugeRenderer's private constants.
    /// </summary>
    static void AssertRingHasNoGaps(Bitmap icon)
    {
        int w = icon.Width, h = icon.Height;
        double cx = w / 2.0, cy = h / 2.0;

        const int buckets = 24; // 15 degrees each -- coarser than this would miss the old {2.2, 2.0} dash gaps
        var hit = new bool[buckets];
        int totalRingPixels = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color px = icon.GetPixel(x, y);
                if (px.A < 150) continue; // comfortably below the dimmed ring's own alpha ceiling
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < w * 0.30) continue; // exclude the inner pie/erased gap -- the ring lives in the outer annulus
                totalRingPixels++;

                double angleDeg = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
                int bucket = (int)(angleDeg / (360.0 / buckets)) % buckets;
                hit[bucket] = true;
            }
        }

        Assert.True(totalRingPixels > 0, "no ring pixels found in the outer annulus");
        for (int b = 0; b < buckets; b++)
        {
            Assert.True(hit[b],
                $"ring has a gap at bucket {b} ({b * 360.0 / buckets:F0}-{(b + 1) * 360.0 / buckets:F0} deg) -- " +
                "expected a continuous ring, not a dashed one");
        }
    }

    static Color Blend(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        int r = (int)Math.Round(fg.R * a + bg.R * (1 - a));
        int g = (int)Math.Round(fg.G * a + bg.G * (1 - a));
        int b = (int)Math.Round(fg.B * a + bg.B * (1 - a));
        return Color.FromArgb(r, g, b);
    }

    static double RelativeLuminance(Color c)
    {
        double r = Channel(c.R), g = Channel(c.G), b = Channel(c.B);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;

        static double Channel(byte v)
        {
            double cv = v / 255.0;
            return cv <= 0.04045 ? cv / 12.92 : Math.Pow((cv + 0.055) / 1.055, 2.4);
        }
    }

    static double ContrastRatio(double l1, double l2)
    {
        double lighter = Math.Max(l1, l2), darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }
}

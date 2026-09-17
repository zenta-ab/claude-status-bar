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
            LastChangedAt: UtcNow, LastPollAt: UtcNow, PollInterval: TimeSpan.FromSeconds(300),
            Error: null, IconSeverity: QuotaState.Spent, BlockedUntil: session.ResetsAt);
    }

    [Fact]
    public void ExhaustedIcon_At16px_OnDarkTaskbar_MeetsMinimumContrast()
    {
        // 60 min into the 180-min countdown: the pie is partially filled, not empty/full,
        // so both the ring AND the pie wedge have real pixels to measure.
        QuotaIconParams p = QuotaIconParams.Build(SpentView(), px: 16, taskbarDark: true, UtcNow.AddMinutes(60));
        Assert.True(p.Exhausted);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        (double MinContrast, int Count, Color WorstPixel, int Wx, int Wy) result = MeasureMinContrast(icon, DarkTaskbar);

        Assert.True(result.Count > 0, "no near-fully-opaque glyph core pixels found to measure -- the ring/pie may be sub-pixel at 16px");
        Assert.True(result.MinContrast >= 3.0,
            $"exhausted icon contrast on #202020 at 16px measured {result.MinContrast:F2}:1 over {result.Count} px, need >= 3:1 " +
            $"(worst pixel at ({result.Wx},{result.Wy}): A={result.WorstPixel.A} R={result.WorstPixel.R} G={result.WorstPixel.G} B={result.WorstPixel.B})");
    }

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

        double spentContrast = MeasureMinContrast(spent, DarkTaskbar).MinContrast;
        double tightContrast = MeasureMinContrast(tight, DarkTaskbar).MinContrast;
        double safeContrast = MeasureMinContrast(safe, DarkTaskbar).MinContrast;

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
            LastChangedAt: UtcNow, LastPollAt: UtcNow, PollInterval: TimeSpan.FromSeconds(300),
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

    static (double MinContrast, int Count, Color WorstPixel, int Wx, int Wy) MeasureMinContrast(Bitmap icon, Color background)
    {
        double minContrast = double.PositiveInfinity;
        int count = 0;
        Color worst = default;
        int wx = -1, wy = -1;
        for (int y = 0; y < icon.Height; y++)
        {
            for (int x = 0; x < icon.Width; x++)
            {
                Color px = icon.GetPixel(x, y);
                // Anti-aliasing puts a smooth alpha gradient across every stroke edge; at any
                // cutoff there will always be some pixel sitting just above it. What "the
                // colour" actually looks like -- and what the spec's 5:1 requirement is about
                // -- is the near-fully-opaque core of the stroke/pie, not the 1px AA fringe.
                // The exhausted state's own dimAlpha (0.85 on a dark taskbar) already caps
                // every pixel's deliverable alpha at round(0.85*255)=217 by design, so the
                // core-pixel cutoff has to sit just below that ceiling, not near 255.
                if (px.A < 208) continue;

                Color blended = Blend(px, background);
                double contrast = ContrastRatio(RelativeLuminance(blended), RelativeLuminance(background));
                count++;
                if (contrast < minContrast) { minContrast = contrast; worst = px; wx = x; wy = y; }
            }
        }
        return (minContrast, count, worst, wx, wy);
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

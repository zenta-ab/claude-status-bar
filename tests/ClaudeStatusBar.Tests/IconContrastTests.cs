using System.Drawing;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/forecast-and-states.md, "Exhausted": the dashed ring / countdown pie must
/// measure >= 5:1 contrast against a dark taskbar (#202020) at 16px -- the earlier
/// #6C7480 at 42% alpha measured only 1.71:1. This is the actual pixel measurement
/// the doc asks for, not hand math: it renders the real icon and blends every
/// solid glyph pixel against #202020 using the WCAG contrast formula.
/// </summary>
public class IconContrastTests
{
    static readonly Color DarkTaskbar = Color.FromArgb(255, 0x20, 0x20, 0x20);

    [Fact]
    public void ExhaustedIcon_At16px_OnDarkTaskbar_MeetsMinimumContrast()
    {
        DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var session = new WindowView(
            WindowKind.Session, QuotaWindows.SessionMinutes, 99.7, utcNow.AddMinutes(180), QuotaState.Spent,
            RatePctPerMin: 0.4, PaceMultiple: 1.2, ProjectedPctAtReset: 100, DepletesAt: null,
            Shortfall: TimeSpan.Zero, MeasuringReason: null);
        WindowView weekly = WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes);

        var view = new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow, LastPollAt: utcNow, PollInterval: TimeSpan.FromSeconds(300),
            Error: null, IconSeverity: QuotaState.Spent, BlockedUntil: session.ResetsAt);

        // 60 min into the 180-min countdown: the pie is partially filled, not empty/full,
        // so both the dashed ring AND the pie wedge have real pixels to measure.
        QuotaIconParams p = QuotaIconParams.Build(view, px: 16, taskbarDark: true, utcNow.AddMinutes(60));
        Assert.True(p.Exhausted);

        using Bitmap icon = GaugeRenderer.RenderQuota(p);
        (double MinContrast, int Count, Color WorstPixel, int Wx, int Wy) result = MeasureMinContrast(icon, DarkTaskbar);

        Assert.True(result.Count > 0, "no near-fully-opaque glyph core pixels found to measure -- the dashed ring/pie may be sub-pixel at 16px");
        Assert.True(result.MinContrast >= 5.0,
            $"exhausted icon contrast on #202020 at 16px measured {result.MinContrast:F2}:1 over {result.Count} px, need >= 5:1 " +
            $"(worst pixel at ({result.Wx},{result.Wy}): A={result.WorstPixel.A} R={result.WorstPixel.R} G={result.WorstPixel.G} B={result.WorstPixel.B})");
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

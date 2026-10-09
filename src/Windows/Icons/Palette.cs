using System.Drawing;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// The agreed colour ramp (docs/forecast-and-states.md, "Colours"). "Dead" is the
/// exhausted-state grey; it must stay #9AA3AE, NOT the earlier #6C7480, which
/// measured 1.71:1 contrast against a dark taskbar (invisible). "Measuring" is a
/// neutral grey distinct from Dead, per the same section.
/// </summary>
public static class Palette
{
    public static readonly Color Ok = Color.FromArgb(255, 0x12, 0xA2, 0x77);       // Safe
    public static readonly Color Tight = Color.FromArgb(255, 0xE8, 0xA0, 0x20);
    public static readonly Color Crit = Color.FromArgb(255, 0xE2, 0x3D, 0x28);     // DryEarly
    public static readonly Color Dead = Color.FromArgb(255, 0x9A, 0xA3, 0xAE);     // Spent
    public static readonly Color Measuring = Color.FromArgb(255, 0x5C, 0x66, 0x72); // distinct from Dead

    /// <summary>The light taskbar's colour (#F3F3F3), the backdrop the light-theme glyph colours are checked against.</summary>
    public static readonly Color LightTaskbar = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

    /// <summary>Target contrast (WCAG ratio) of a glyph's core against the light taskbar: the 3:1 floor plus a little margin.</summary>
    const double LightTargetContrast = 3.15;

    /// <summary>
    /// The colour a tray glyph uses on this taskbar. The palette is tuned for the dark taskbar; on a light
    /// one (#F3F3F3) amber, green and grey fall to 1.6-2.8:1, so the colour is darkened -- hue kept, only
    /// scaled towards black -- just far enough that the glyph's core, composited at `alpha` (the exhausted
    /// glyph's quiet-down), still measures >= 3:1. Unchanged on a dark taskbar.
    /// </summary>
    public static Color ForTaskbar(Color color, bool taskbarDark, double alpha = 1.0)
    {
        if (taskbarDark) return color;
        for (double f = 1.0; f >= 0.30; f -= 0.02)
        {
            Color scaled = Color.FromArgb(color.A, (int)(color.R * f), (int)(color.G * f), (int)(color.B * f));
            if (Contrast(scaled, alpha, LightTaskbar) >= LightTargetContrast) return scaled;
        }
        return Color.FromArgb(color.A, (int)(color.R * 0.30), (int)(color.G * 0.30), (int)(color.B * 0.30));
    }

    /// <summary>WCAG contrast ratio of `fg` composited at `alpha` over an opaque `bg`.</summary>
    public static double Contrast(Color fg, double alpha, Color bg)
    {
        double r = fg.R * alpha + bg.R * (1 - alpha), g = fg.G * alpha + bg.G * (1 - alpha), b = fg.B * alpha + bg.B * (1 - alpha);
        double l1 = Luminance(r, g, b), l2 = Luminance(bg.R, bg.G, bg.B);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

        static double Luminance(double r, double g, double b)
        {
            static double Channel(double v)
            {
                double c = v / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }
    }

    /// <summary>Maps a window's committed QuotaState to its render colour.</summary>
    public static Color ColorForState(QuotaState state) => state switch
    {
        QuotaState.Safe => Ok,
        QuotaState.Tight => Tight,
        QuotaState.DryEarly => Crit,
        QuotaState.Spent => Dead,
        _ => Measuring,
    };
}

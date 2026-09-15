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

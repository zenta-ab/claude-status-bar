using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Microsoft.Win32;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// Owns the Icon lifecycle for one NotifyIcon. Renders natively at the DPI-correct
/// tray icon size, builds a self-owned Icon (see IconFactory), assigns it to the
/// NotifyIcon FIRST, then retires the old one -- keeping two generations alive so
/// the shell never briefly sees a destroyed handle. At most 3 Icon instances exist
/// at any instant (the incoming icon, the generation it displaces, and that
/// generation's predecessor mid-Dispose); this ceiling holds forever, one Update
/// call at a time.
///
/// Driven by the 1 Hz UI tick (docs/forecast-and-states.md, "Icon"): re-renders
/// only when the quantized render inputs actually change, and at most every 15s
/// while any window is Spent (the countdown pie would otherwise churn every tick).
/// </summary>
public sealed class IconSlot : IDisposable
{
    static readonly TimeSpan ExhaustedThrottle = TimeSpan.FromSeconds(15);

    readonly NotifyIcon _notifyIcon;
    Icon? _newest;
    Icon? _previous;
    QuotaIconParams? _lastParams;
    long _lastRenderAtMonoMs = long.MinValue;

    public IconSlot(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public void Update(QuotaView view)
    {
        try
        {
            int px = QueryTrayIconPixelSize();
            bool taskbarDark = TaskbarIsDark();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // Monotonic (Codex review Low #18): a backward wall-clock jump must not leave the
            // exhausted-icon throttle stuck open/closed until wall time catches back up.
            long nowMonoMs = Environment.TickCount64;

            QuotaIconParams p = QuotaIconParams.Build(view, px, taskbarDark, now);

            bool sizeOrThemeChanged = _lastParams is not { } last || last.Px != p.Px || last.TaskbarDark != p.TaskbarDark;
            bool throttledExhausted = !sizeOrThemeChanged && p.Exhausted && (nowMonoMs - _lastRenderAtMonoMs) < ExhaustedThrottle.TotalMilliseconds;
            bool unchanged = !sizeOrThemeChanged && !p.Exhausted && _lastParams is { } same && same.Equals(p);

            if (!sizeOrThemeChanged && (throttledExhausted || unchanged))
            {
                _notifyIcon.Text = BuildTooltip(view);
                return;
            }

            Icon next;
            using (Bitmap bmp = GaugeRenderer.RenderQuota(p))
            {
                byte[] icoBytes = IconFactory.BuildIco(bmp);
                using var ms = new MemoryStream(icoBytes);
                next = new Icon(ms, new Size(px, px));
            }

            try
            {
                Assign(next);
            }
            catch
            {
                // Ownership stays consistent even on failure (Codex review Medium #16): if
                // assignment itself throws, the newly-built icon we just created is not yet
                // owned by _newest/_previous, so it must be disposed here or it leaks.
                next.Dispose();
                throw;
            }

            _lastParams = p;
            _lastRenderAtMonoMs = nowMonoMs;
            _notifyIcon.Text = BuildTooltip(view);
        }
        catch (Exception ex)
        {
            // UI exception boundary (Codex review Medium #15): keep the last good icon/text
            // on any render failure rather than crashing the 1Hz eval tick that drives this.
            SafeLog.Warn($"IconSlot.Update failed, keeping last icon: {ex.Message}");
        }
    }

    /// <summary>
    /// Tray tooltip (docs/panel-v2.md, "Tray tooltip"): the same verdict and time as
    /// the panel's status box, condensed to fit NotifyIcon.Text's 63-character hard
    /// cap (a WinForms runtime limit -- it throws above that, not just truncates
    /// silently). Driven by whichever window is most severe, same tie-break as
    /// PanelText.Compose (session wins ties), built entirely through TimeText so
    /// no raw minute count can leak into it either.
    /// </summary>
    static string BuildTooltip(QuotaView view)
    {
        string text = ComposeTooltip(view);
        return text.Length > 63 ? text[..63] : text;
    }

    static string ComposeTooltip(QuotaView view)
    {
        TimeZoneInfo tz = TimeZoneInfo.Local;

        if (view.Freshness == Freshness.Unknown) return "Kan inte läsa kvoten";

        if (view.BlockedUntil is { } blockedUntil)
            return $"Kvoten slut · öppnar {TimeText.ClockOnly(blockedUntil, tz)}";

        WindowView driver = (int)view.Session.State >= (int)view.Weekly.State ? view.Session : view.Weekly;

        return driver.State switch
        {
            QuotaState.DryEarly when driver.DepletesAt is { } dep =>
                $"Slut kl {TimeText.ClockOnly(dep, tz)} — {TimeText.Duration(driver.Shortfall)} före reset",
            QuotaState.DryEarly => "Kvoten nästan slut",
            QuotaState.Tight when driver.ResetsAt is { } r =>
                $"Tajt · ~{driver.ProjectedPctAtReset ?? driver.UsedPct ?? 0.0:F0}% vid reset {TimeText.ClockOnly(r, tz)}",
            QuotaState.Measuring when driver.ResetsAt is { } r => $"Mäter takt · reset {TimeText.ClockOnly(r, tz)}",
            QuotaState.Measuring => "Mäter takt…",
            _ when driver.ResetsAt is { } r =>
                $"Räcker · ~{driver.ProjectedPctAtReset ?? driver.UsedPct ?? 0.0:F0}% vid reset {TimeText.ClockOnly(r, tz)}",
            _ => "Hämtar…",
        };
    }

    void Assign(Icon next)
    {
        _notifyIcon.Icon = next; // assign first: the shell must never see a null/destroyed icon
        Icon? toRetire = _previous;
        _previous = _newest;
        _newest = next;
        toRetire?.Dispose(); // self-owned handle (see IconFactory) -> Dispose really calls DestroyIcon
    }

    public void Dispose()
    {
        _newest?.Dispose();
        _previous?.Dispose();
        _newest = null;
        _previous = null;
    }

    /// <summary>
    /// FindWindow(Shell_TrayWnd) -> GetDpiForWindow -> GetSystemMetricsForDpi(SM_CXSMICON).
    /// Plain GetSystemMetrics(SM_CXSMICON) reports the *process's* DPI awareness, which on
    /// this machine returns 16 regardless of the actual taskbar scale -- measured wrong.
    /// </summary>
    static int QueryTrayIconPixelSize()
    {
        const int SM_CXSMICON = 49;
        const int fallback = 16;

        IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (trayWnd == IntPtr.Zero) return fallback;

        uint dpi = NativeMethods.GetDpiForWindow(trayWnd);
        if (dpi == 0) return fallback;

        int px = NativeMethods.GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        return px is > 0 and <= 256 ? px : fallback;
    }

    /// <summary>
    /// HKCU Personalize\SystemUsesLightTheme drives the taskbar's own colour (distinct
    /// from AppsUseLightTheme, which is app-window chrome). Missing key -> assume dark,
    /// the far more common taskbar default.
    /// </summary>
    static bool TaskbarIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("SystemUsesLightTheme");
            if (value is int i) return i == 0;
        }
        catch
        {
            // registry access can fail under odd policy configs; fall through to the default.
        }
        return true;
    }

    static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
    }
}

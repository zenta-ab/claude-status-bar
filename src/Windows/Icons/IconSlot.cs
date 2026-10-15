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
/// only when the render inputs actually change (the exhausted icon's countdown text is one of them:
/// it changes at most once a minute), and, while any window is Spent, never more often than every 15 s.
/// </summary>
public sealed class IconSlot : IDisposable
{
    /// <summary>The least time between two renders of the exhausted icon. Its text changes at most once a minute, so this never delays a change by more than itself and exists only to cap a flapping input.</summary>
    internal static readonly TimeSpan ExhaustedThrottle = TimeSpan.FromSeconds(15);

    readonly NotifyIcon _notifyIcon;
    Icon? _newest;
    Icon? _previous;
    QuotaIconParams? _lastParams;
    long _lastRenderAtMonoMs = long.MinValue;

    public IconSlot(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    /// <param name="view">What to render.</param>
    /// <param name="accountLabel">
    /// docs/multi-account.md "Display": when set, the tooltip is prefixed with this account's
    /// label (truncating the LABEL, never the verdict, to stay within NotifyIcon.Text's 63-char
    /// cap). Null (the default) keeps the exact single-account tooltip this app has always shown
    /// -- required so a single-account install's icon is unchanged.
    /// </param>
    /// <param name="tooltipOverride">
    /// Replaces the whole tooltip -- used by the zero-accounts icon (docs/multi-account.md "Zero
    /// accounts"), which has no verdict to describe. Must fit NotifyIcon.Text's 63-character cap.
    /// </param>
    /// <param name="nowOverride">Test / self-test only: the instant to render for, so the loading icon's frames can be stepped without waiting for real time.</param>
    public void Update(QuotaView view, string? accountLabel = null, string? tooltipOverride = null, DateTimeOffset? nowOverride = null)
    {
        try
        {
            int px = QueryTrayIconPixelSize();
            bool taskbarDark = TaskbarIsDark();
            DateTimeOffset now = nowOverride ?? DateTimeOffset.UtcNow;
            // Monotonic (Codex review Low #18): a backward wall-clock jump must not leave the
            // exhausted-icon throttle stuck open/closed until wall time catches back up.
            long nowMonoMs = nowOverride is { } stepped ? stepped.ToUnixTimeMilliseconds() : Environment.TickCount64; // a stepped (test) clock drives the throttle too

            QuotaIconParams p = QuotaIconParams.Build(view, px, taskbarDark, now);

            if (!ShouldRender(_lastParams, p, nowMonoMs, _lastRenderAtMonoMs))
            {
                _notifyIcon.Text = tooltipOverride ?? BuildTooltip(view, accountLabel);
                return;
            }

            Icon next;
            if (p.Loading)
            {
                // A cached, ready-made frame: only an Icon is built, nothing is drawn.
                using var ms = new MemoryStream(LoadingFrames.GetIcoBytes(px, p.LoadingFrame, p.TaskbarDark), writable: false);
                next = new Icon(ms, new Size(px, px));
            }
            else
            {
                using Bitmap bmp = GaugeRenderer.RenderQuota(p);
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
            _notifyIcon.Text = tooltipOverride ?? BuildTooltip(view, accountLabel);
        }
        catch (Exception ex)
        {
            // UI exception boundary (Codex review Medium #15): keep the last good icon/text
            // on any render failure rather than crashing the 1Hz eval tick that drives this.
            SafeLog.Warn($"IconSlot.Update failed, keeping last icon: {ex.Message}");
        }
    }

    /// <summary>
    /// Does this tick need a new Icon? Yes for the first one and whenever the tray size or taskbar theme
    /// changed (always); no when the params are identical to what is shown -- the exhausted icon included,
    /// its countdown text being part of them, so it is re-rendered exactly when the text changes (once a
    /// minute at most); and an exhausted change is held back until ExhaustedThrottle has passed since the
    /// last render (it is then rendered on the first tick after, because the params still differ).
    /// </summary>
    internal static bool ShouldRender(QuotaIconParams? last, QuotaIconParams next, long nowMonoMs, long lastRenderMonoMs)
    {
        if (last is not { } l || l.Px != next.Px || l.TaskbarDark != next.TaskbarDark) return true;
        if (l.Equals(next)) return false;
        return !(next.Exhausted && nowMonoMs - lastRenderMonoMs < ExhaustedThrottle.TotalMilliseconds);
    }

    const int TooltipMaxLength = 63;

    /// <summary>
    /// Tray tooltip (docs/panel-v2.md, "Tray tooltip"): the same verdict and time as
    /// the panel's status box, condensed to fit NotifyIcon.Text's 63-character hard
    /// cap (a WinForms runtime limit -- it throws above that, not just truncates
    /// silently). Driven by whichever window is most severe, same tie-break as
    /// PanelText.Compose (session wins ties), built entirely through TimeText so
    /// no raw minute count can leak into it either.
    ///
    /// Multi-account (docs/multi-account.md "Display"): with a non-null accountLabel, the
    /// tooltip starts with it ("{label} · {verdict}"), so the wearer can tell which account an
    /// icon is. If that would overflow the 63-char cap, the LABEL is truncated, never the
    /// verdict -- the verdict is the part answering "will I hit a wall", the label is just which
    /// account, so it is the one that can lose characters.
    /// </summary>
    /// <param name="now">Test seam (default: the real clock).</param>
    /// <param name="tz">Test seam (default: local time).</param>
    internal static string BuildTooltip(QuotaView view, string? accountLabel, DateTimeOffset? now = null, TimeZoneInfo? tz = null)
    {
        string verdict = ComposeTooltip(view, now ?? DateTimeOffset.UtcNow, tz ?? TimeZoneInfo.Local);
        string truncatedVerdict = verdict.Length > TooltipMaxLength ? verdict[..TooltipMaxLength] : verdict;
        if (string.IsNullOrEmpty(accountLabel)) return truncatedVerdict;

        const string separator = " · ";
        string full = $"{accountLabel}{separator}{verdict}";
        if (full.Length <= TooltipMaxLength) return full;

        int labelBudget = TooltipMaxLength - separator.Length - verdict.Length;
        if (labelBudget < 1) return truncatedVerdict; // verdict alone already fills (or exceeds) the cap -- drop the label entirely rather than mangle the verdict

        string truncatedLabel = accountLabel.Length > labelBudget ? accountLabel[..labelBudget] : accountLabel;
        return $"{truncatedLabel}{separator}{verdict}";
    }

    static string ComposeTooltip(QuotaView view, DateTimeOffset now, TimeZoneInfo tz)
    {

        if (view.Loading) return LoginText.LoadingTooltip;

        // docs/multi-account.md "NeedsLogin": the same grey ring as Unknown, but the fix is a login, so say so.
        if (view.NeedsLogin) return LoginText.NeedsLoginTooltip;

        if (view.Freshness == Freshness.Unknown) return "Kan inte läsa kvoten";

        if (view.BlockedUntil is { } blockedUntil)
            return view.Weekly.State == QuotaState.Spent
                ? $"Veckokvoten slut · öppnar {TimeText.ClockWithDay(view.Weekly.ResetsAt ?? blockedUntil, now, tz)}"
                : $"Kvoten slut · öppnar {TimeText.ClockWithDay(view.Session.ResetsAt ?? blockedUntil, now, tz)}";

        WindowView driver = (int)view.Session.State >= (int)view.Weekly.State ? view.Session : view.Weekly;

        return driver.State switch
        {
            QuotaState.DryEarly when driver.DepletesAt is { } dep =>
                $"Slut {TimeText.ClockWithDay(dep, now, tz)} — {TimeText.Duration(driver.Shortfall)} före reset",
            QuotaState.DryEarly => "Kvoten nästan slut",
            // A Tight window with a shortfall runs out before its reset: say when, like DryEarly does (WindowView.ForecastRunOutAt).
            QuotaState.Tight when driver.ForecastRunOutAt is { } tightDep =>
                $"Tajt · slut {TimeText.ClockWithDay(tightDep, now, tz)} — {TimeText.Duration(driver.Shortfall)} före reset",
            QuotaState.Tight when driver.ResetsAt is { } r =>
                $"Tajt · ~{driver.ProjectedPctAtReset ?? driver.UsedPct ?? 0.0:F0}% vid reset {TimeText.ClockWithDay(r, now, tz)}",
            QuotaState.Measuring when driver.ResetsAt is { } r => $"Mäter takt · reset {TimeText.ClockWithDay(r, now, tz)}",
            QuotaState.Measuring => "Mäter takt…",
            _ when driver.ResetsAt is { } r =>
                $"Räcker · ~{driver.ProjectedPctAtReset ?? driver.UsedPct ?? 0.0:F0}% vid reset {TimeText.ClockWithDay(r, now, tz)}",
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
    internal static int QueryTrayIconPixelSize()
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

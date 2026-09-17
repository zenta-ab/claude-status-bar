using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// Computes where to place the panel so it behaves like a native tray flyout:
/// anchored just above the tray icon via Shell_NotifyIconGetRect, falling back
/// to the work area's bottom-right corner when that lookup fails (e.g. the icon
/// lives in the hidden overflow flyout, where Shell_NotifyIconGetRect does not
/// reliably report a rect). The edge nearest the taskbar is placed 12px off it;
/// every other edge is clamped inside the target monitor's work area, so this
/// also copes with a taskbar docked to the top, left or right rather than the
/// (overwhelmingly common) bottom.
/// </summary>
public static class PanelAnchor
{
    const int TaskbarGap = 12;
    const int ScreenMargin = 8;

    /// <summary>
    /// One-stop resolution for showing the panel: which monitor it belongs on
    /// (the tray icon's, or the primary screen as a fallback), that monitor's DPI
    /// scale (panel is a transient popup recreated fresh on every show, so there
    /// is no need to track a live monitor move the way a draggable window would),
    /// and the on-screen location for the resulting physical-pixel size.
    /// </summary>
    public static (Point Location, Size Size, float Scale) Resolve(NotifyIcon? notifyIcon, Size logicalSize)
    {
        Rectangle? iconRect = TryGetIconRect(notifyIcon, out Rectangle r) ? r : null;
        Screen screen = iconRect is { } ir ? Screen.FromRectangle(ir) : Screen.PrimaryScreen ?? Screen.AllScreens[0];

        float scale = GetDpiScale(screen);
        var physicalSize = new Size(
            (int)Math.Round(logicalSize.Width * scale),
            (int)Math.Round(logicalSize.Height * scale));
        Point location = Compute(iconRect, screen.Bounds, screen.WorkingArea, physicalSize);
        return (location, physicalSize, scale);
    }

    /// <summary>
    /// Per-monitor DPI via Shcore, the same source Windows itself uses for
    /// per-monitor-v2 scaling. Falls back to 100% (scale 1) if the monitor
    /// handle or the API call fails -- a wrong DPI guess degrades to a
    /// slightly mis-sized panel, never a crash.
    /// </summary>
    static float GetDpiScale(Screen screen)
    {
        try
        {
            var rect = new RECT
            {
                Left = screen.Bounds.Left, Top = screen.Bounds.Top,
                Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom,
            };
            IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return 1f;

            int hr = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
            return hr == 0 && dpiX > 0 ? dpiX / 96f : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    /// <summary>Pure geometry, split out from Compute() so anchor placement can be exercised without a real NotifyIcon.</summary>
    internal static Point Compute(Rectangle? iconRect, Rectangle screenBounds, Rectangle workArea, Size panelSize)
    {
        Point anchor = iconRect is { } ir
            ? new Point(ir.X + ir.Width / 2, ir.Y + ir.Height / 2)
            : new Point(workArea.Right, workArea.Bottom);

        int x, y;
        switch (TaskbarEdge(screenBounds, workArea))
        {
            case Edge.Top:
                y = workArea.Top + TaskbarGap;
                x = anchor.X - panelSize.Width / 2;
                break;
            case Edge.Right:
                x = workArea.Right - TaskbarGap - panelSize.Width;
                y = anchor.Y - panelSize.Height / 2;
                break;
            case Edge.Left:
                x = workArea.Left + TaskbarGap;
                y = anchor.Y - panelSize.Height / 2;
                break;
            default: // bottom, or no taskbar edge detected -- the common case
                y = workArea.Bottom - TaskbarGap - panelSize.Height;
                x = anchor.X - panelSize.Width / 2;
                break;
        }

        return new Point(
            ClampAxis(x, workArea.Left + ScreenMargin, workArea.Right - ScreenMargin - panelSize.Width),
            ClampAxis(y, workArea.Top + ScreenMargin, workArea.Bottom - ScreenMargin - panelSize.Height));
    }

    /// <summary>Math.Clamp throws when min > max (panel wider/taller than the work area); this degrades to min instead.</summary>
    static int ClampAxis(int value, int min, int max) =>
        max < min ? min : Math.Clamp(value, min, max);

    enum Edge { Bottom, Top, Left, Right }

    /// <summary>The work area is the screen bounds minus the taskbar (and any other app-bars); the side that shrank names the edge.</summary>
    static Edge TaskbarEdge(Rectangle screenBounds, Rectangle workArea)
    {
        if (workArea.Top > screenBounds.Top) return Edge.Top;
        if (workArea.Left > screenBounds.Left) return Edge.Left;
        if (workArea.Right < screenBounds.Right) return Edge.Right;
        return Edge.Bottom; // also the fallback when work area == screen bounds (taskbar set to auto-hide)
    }

    /// <summary>
    /// NotifyIcon does not expose its native hWnd/id, so this reflects into the
    /// private fields that back Shell_NotifyIcon calls (confirmed present on the
    /// net10.0-windows WinForms runtime this app targets). Any failure -- field
    /// renamed, icon still in the overflow flyout, whatever -- degrades to the
    /// bottom-right fallback rather than throwing; an anchor lookup must never
    /// be able to crash the app.
    /// </summary>
    /// <summary>
    /// Internal (not just private): DismissWatcher/PanelForm reuse this same lookup to exclude
    /// the tray icon's own rectangle from click-away dismissal (Codex review Medium #13).
    /// notifyIcon is nullable (docs/multi-account.md): PanelForm's anchor icon can be unset for
    /// an instant while icons are being reconciled (e.g. every account momentarily disabled) --
    /// that degrades to "no rect", never a null-reference.
    /// </summary>
    internal static bool TryGetIconRect(NotifyIcon? notifyIcon, out Rectangle rect)
    {
        rect = default;
        if (notifyIcon is null) return false;
        try
        {
            FieldInfo? windowField = typeof(NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? idField = typeof(NotifyIcon).GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance);
            object? window = windowField?.GetValue(notifyIcon);
            if (window is null || idField is null) return false;

            if (window.GetType().GetProperty("Handle")?.GetValue(window) is not IntPtr hwnd || hwnd == IntPtr.Zero)
                return false;
            if (idField.GetValue(notifyIcon) is not uint id)
                return false;

            var identifier = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = hwnd,
                uID = id,
            };

            int hr = Shell_NotifyIconGetRect(ref identifier, out RECT r);
            if (hr != 0 /* S_OK */) return false;

            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch
        {
            return false; // reflection shape or shell behaviour can change under us; never take the app down over an anchor
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("Shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

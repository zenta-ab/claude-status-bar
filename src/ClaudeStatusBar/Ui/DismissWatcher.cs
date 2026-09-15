using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// Detects click-away and focus-elsewhere so the panel can close the way a
/// native flyout does, even though it never takes focus itself (WS_EX_NOACTIVATE
/// means normal focus/deactivate events never fire for it). Three low-level
/// global hooks, installed ONLY while Start() is active and removed the instant
/// Stop()/Dispose() runs:
///   - WH_MOUSE_LL: a left/right click landing outside the panel's bounds.
///   - WH_KEYBOARD_LL: Esc, since the panel can't receive WM_KEYDOWN via normal
///     focus-based routing without taking focus.
///   - EVENT_SYSTEM_FOREGROUND: any other window becoming foreground (alt-tab,
///     clicking a taskbar button, etc) -- the panel is never itself foreground,
///     so any occurrence of this event means the user moved away from it.
/// A leaked WH_MOUSE_LL/WH_KEYBOARD_LL hook affects every window on the desktop,
/// not just this app, so these must never outlive a visible panel.
///
/// Hardening (Codex review High #11, Medium #12, Medium #13):
///   - Every native callback has its own try/catch and always returns promptly
///     (CallNextHookEx for the two hook procs); a slow or throwing subscriber must
///     never delay desktop input or let Windows silently drop the hook.
///   - Dismissal is coalesced and posted to the owning Control via BeginInvoke
///     rather than run inline on the hook callback's own stack.
///   - Start() is transactional: if any of the three installs fails, whatever
///     already succeeded is rolled back and _watching stays false.
///   - Stop() inspects the actual handles, not the _watching flag, so a partial
///     install (or Stop() called twice) still tears down everything that is
///     actually installed.
///   - An optional isExcluded test (the tray icon's own rectangle) lets a click
///     that will be handled by the tray icon's own click handler skip dismissal,
///     so a single click to close the panel cannot also immediately reopen it.
/// </summary>
public sealed class DismissWatcher : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int WH_KEYBOARD_LL = 13;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int VK_ESCAPE = 0x1B;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public event Action? DismissRequested;

    readonly Control _owner;

    // Delegates are kept alive in fields for as long as the hook is installed --
    // otherwise the GC is free to collect them while native code still holds a
    // function pointer into them.
    HookProc? _mouseProc;
    HookProc? _keyboardProc;
    WinEventProc? _winEventProc;
    IntPtr _mouseHook;
    IntPtr _keyboardHook;
    IntPtr _winEventHook;
    Func<Point, bool>? _isInsidePanel;
    Func<Point, bool>? _isExcluded;
    bool _watching;
    volatile bool _dismissPosted;

    /// <param name="owner">The Control whose message loop dismissal is marshalled onto (PanelForm). Never touched off that thread.</param>
    public DismissWatcher(Control owner)
    {
        _owner = owner;
    }

    /// <param name="isInsidePanel">Screen-point hit test against the panel's current bounds.</param>
    /// <param name="isExcluded">Screen-point hit test for a region that must never trigger dismissal even though it's outside the panel -- the tray icon's own rectangle, so the tray's own click handler owns that click instead of a hide-then-reopen race (Codex review Medium #13).</param>
    /// <returns>False if any hook failed to install; already-installed hooks are rolled back before returning.</returns>
    public bool Start(Func<Point, bool> isInsidePanel, Func<Point, bool>? isExcluded = null)
    {
        if (_watching) return true;

        try
        {
            _isInsidePanel = isInsidePanel;
            _isExcluded = isExcluded;

            _mouseProc = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero) { RollBack(); return false; }

            _keyboardProc = KeyboardHookProc;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero) { RollBack(); return false; }

            _winEventProc = WinEventHookProc;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_winEventHook == IntPtr.Zero) { RollBack(); return false; }

            _watching = true;
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"DismissWatcher.Start threw: {ex.Message}");
            RollBack();
            return false;
        }
    }

    /// <summary>Inspects the actual handles, independent of _watching (Codex review Medium #12): a partial install must still be fully torn down.</summary>
    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_mouseHook)) SafeLog.Warn($"UnhookWindowsHookEx(mouse) failed, GetLastWin32Error={Marshal.GetLastWin32Error()}");
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_keyboardHook)) SafeLog.Warn($"UnhookWindowsHookEx(keyboard) failed, GetLastWin32Error={Marshal.GetLastWin32Error()}");
            _keyboardHook = IntPtr.Zero;
        }
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _mouseProc = null;
        _keyboardProc = null;
        _winEventProc = null;
        _isInsidePanel = null;
        _isExcluded = null;
        _watching = false;
    }

    void RollBack() => Stop();

    /// <summary>Coalesced: multiple hook triggers before the posted callback runs collapse into one DismissRequested invocation.</summary>
    void RequestDismiss()
    {
        if (_dismissPosted) return;
        _dismissPosted = true;
        try
        {
            _owner.BeginInvoke(new Action(() =>
            {
                _dismissPosted = false;
                try { DismissRequested?.Invoke(); }
                catch (Exception ex) { SafeLog.Warn($"DismissRequested subscriber threw: {ex.Message}"); }
            }));
        }
        catch (Exception ex)
        {
            // e.g. the owning handle was already destroyed -- never let posting the
            // dismissal itself take the app down.
            _dismissPosted = false;
            SafeLog.Warn($"DismissWatcher: BeginInvoke failed: {ex.Message}");
        }
    }

    IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(hookStruct.pt.x, hookStruct.pt.y);
                if (ShouldDismissForClick(pt, _isInsidePanel, _isExcluded)) RequestDismiss();
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"MouseHookProc threw: {ex.Message}");
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Pure decision extracted out of MouseHookProc so it is directly unit-testable without a
    /// real low-level hook or desktop session (Codex review Medium #13): a click dismisses
    /// the panel only when it lands outside BOTH the panel's own bounds and the excluded
    /// region (the tray icon's rectangle). Excluding the tray icon is what stops "click the
    /// tray icon to close the open panel" from immediately reopening it -- without the
    /// exclusion, that mousedown reads as "outside the panel", hides it, and the tray's own
    /// MouseClick handler then sees a hidden panel and reopens it.
    /// </summary>
    public static bool ShouldDismissForClick(Point pt, Func<Point, bool>? isInsidePanel, Func<Point, bool>? isExcluded)
    {
        bool insidePanel = isInsidePanel is { } test && test(pt);
        if (insidePanel) return false;
        bool insideExcluded = isExcluded is { } excl && excl(pt);
        return !insideExcluded;
    }

    IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (hookStruct.vkCode == VK_ESCAPE) RequestDismiss();
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"KeyboardHookProc threw: {ex.Message}");
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    void WinEventHookProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try { RequestDismiss(); }
        catch (Exception ex) { SafeLog.Warn($"WinEventHookProc threw: {ex.Message}"); }
    }

    public void Dispose() => Stop();

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
}

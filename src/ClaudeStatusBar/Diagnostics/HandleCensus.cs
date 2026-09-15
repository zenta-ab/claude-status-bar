using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Diagnostics;

/// <summary>
/// GDI/USER handle accounting for the current process, plus the --selftest driver:
/// 5000 render+assign+retire cycles through IconSlot, checking the handle delta
/// stays near zero. This is the M1 regression test pulled forward, since it costs
/// ~30 lines and proves the icon pipeline (the whole reason IconFactory/IconSlot
/// exist) immediately rather than trusting it by inspection.
/// </summary>
public static class HandleCensus
{
    const int GR_GDIOBJECTS = 0;
    const int GR_USEROBJECTS = 1;

    public static (int gdi, int user) Sample()
    {
        IntPtr process = NativeMethods.GetCurrentProcess();
        return (
            NativeMethods.GetGuiResources(process, GR_GDIOBJECTS),
            NativeMethods.GetGuiResources(process, GR_USEROBJECTS));
    }

    /// <summary>Returns 0 if the GDI/USER delta stayed within threshold, 1 otherwise.</summary>
    public static int RunSelfTest(int iterations = 5000, int threshold = 8)
    {
        using var notifyIcon = new NotifyIcon { Visible = false };
        using var slot = new IconSlot(notifyIcon);

        // Two distinct views, alternated below: IconSlot re-renders only when its
        // quantized inputs change (docs/forecast-and-states.md, "Icon"), and this
        // test's whole point is to hammer the render+assign+retire cycle -- a
        // single unchanging QuotaView would make every call after the first a
        // no-op and prove nothing about IconFactory/Icon lifecycle churn.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaView viewA = DemoQuotaSource.Build(now).First(s => s.Key == "tight").View;
        QuotaView viewB = DemoQuotaSource.Build(now).First(s => s.Key == "dry_early").View;

        // warm-up: JIT, GDI+ lazy init, etc, so the measured window is steady-state.
        for (int i = 0; i < 25; i++) slot.Update(i % 2 == 0 ? viewA : viewB);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = Sample();

        for (int i = 0; i < iterations; i++) slot.Update(i % 2 == 0 ? viewA : viewB);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = Sample();

        int dGdi = after.gdi - before.gdi;
        int dUser = after.user - before.user;
        Console.WriteLine(
            $"selftest n={iterations}  GDI {before.gdi}->{after.gdi} (d={dGdi})  " +
            $"USER {before.user}->{after.user} (d={dUser})  threshold={threshold}");

        return Math.Abs(dGdi) <= threshold && Math.Abs(dUser) <= threshold ? 0 : 1;
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();
    }
}

using System.Drawing;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Codex review Medium #13: clicking the tray icon while the panel is open must close it and
/// must NOT immediately reopen it. The real end-to-end behaviour needs a live low-level mouse
/// hook plus the tray's own MouseClick handler racing a background thread's dismiss callback,
/// which is not practical to drive headlessly (and this task's environment has its interactive
/// desktop session locked, which blocks SendInput-based verification -- see the task's
/// Decisions doc). DismissWatcher.ShouldDismissForClick is the exact decision the mouse hook
/// makes, extracted as a pure function precisely so this scenario has real automated coverage
/// independent of any of that.
/// </summary>
public class DismissWatcherTests
{
    static readonly Rectangle PanelBounds = new(100, 100, 340, 260);
    static readonly Rectangle TrayIconRect = new(2500, 1360, 20, 20); // bottom-right corner, typical tray position

    static bool InsidePanel(Point p) => PanelBounds.Contains(p);
    static bool InsideTrayIcon(Point p) => TrayIconRect.Contains(p);

    [Fact]
    public void ClickOnTheTrayIcon_WhileThePanelIsOpen_DoesNotDismiss()
    {
        // This is the exact reproduction: the mousedown that opens/closes the panel via the
        // tray icon lands outside the panel's own bounds, so without the exclusion it would
        // read as "click away" and hide the panel -- and then the tray's MouseClick handler,
        // seeing a hidden panel, would reopen it. One click should close it and leave it closed.
        Point trayClick = new(2505, 1365);
        Assert.True(InsideTrayIcon(trayClick));
        Assert.False(InsidePanel(trayClick));

        bool shouldDismiss = DismissWatcher.ShouldDismissForClick(trayClick, InsidePanel, InsideTrayIcon);

        Assert.False(shouldDismiss);
    }

    [Fact]
    public void ClickInsideThePanel_DoesNotDismiss()
    {
        Point insideClick = new(150, 150);
        bool shouldDismiss = DismissWatcher.ShouldDismissForClick(insideClick, InsidePanel, InsideTrayIcon);
        Assert.False(shouldDismiss);
    }

    [Fact]
    public void ClickElsewhereOnTheDesktop_Dismisses()
    {
        Point elsewhere = new(10, 10);
        bool shouldDismiss = DismissWatcher.ShouldDismissForClick(elsewhere, InsidePanel, InsideTrayIcon);
        Assert.True(shouldDismiss);
    }

    [Fact]
    public void NoExclusionProvided_FallsBackToPanelBoundsOnly()
    {
        // ShowPanel degrades to "no exclusion" when the tray icon rect lookup fails (see
        // PanelAnchor.TryGetIconRect's own fallback contract) -- must still behave like the
        // pre-fix logic in that case, not throw.
        Point trayClick = new(2505, 1365);
        bool shouldDismiss = DismissWatcher.ShouldDismissForClick(trayClick, InsidePanel, isExcluded: null);
        Assert.True(shouldDismiss);
    }

    [Fact]
    public void NoInsidePanelTestEitherWayIsProvided_TreatedAsOutside_NeverThrows()
    {
        bool shouldDismiss = DismissWatcher.ShouldDismissForClick(new Point(1, 1), isInsidePanel: null, isExcluded: null);
        Assert.True(shouldDismiss);
    }
}

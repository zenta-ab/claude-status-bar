namespace ClaudeStatusBar.Icons;

/// <summary>
/// One tray icon and its IconSlot render pipeline, owned per shown account in perAccount/binding
/// display mode (docs/multi-account.md "Display"). Bundles the NotifyIcon's own lifecycle with
/// IconSlot's so StatusBarApplicationContext can create and destroy per-account icons at runtime
/// (accounts added/removed, or the display mode switching) without re-deriving IconSlot's own
/// disposal ordering rules (Icon.FromHandle stays banned, retire-after-assign, no leaks -- see
/// IconSlot's own doc comment) at every call site that needs a new or removed icon.
/// </summary>
public sealed class TrayIconHandle : IDisposable
{
    public NotifyIcon NotifyIcon { get; }
    public IconSlot Slot { get; }

    public TrayIconHandle()
    {
        NotifyIcon = new NotifyIcon { Text = "Claude Status Bar", Visible = true };
        Slot = new IconSlot(NotifyIcon);
    }

    public void Dispose()
    {
        try { Slot.Dispose(); } catch { /* best effort */ }
        try { NotifyIcon.Visible = false; } catch { /* best effort */ }
        // The ContextMenuStrip is shared across every TrayIconHandle and owned by
        // StatusBarApplicationContext -- clear the reference here but never dispose it.
        try { NotifyIcon.ContextMenuStrip = null; } catch { /* best effort */ }
        try { NotifyIcon.Dispose(); } catch { /* best effort */ }
    }
}

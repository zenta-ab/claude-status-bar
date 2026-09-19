import AppKit
import ClaudeQuotaCore

/// Hosts `PanelView` in an `NSPopover` anchored to a status item's button.
///
/// `NSPopover` is the right primitive here rather than a borderless window: it already handles
/// the arrow, the menu-bar anchoring, the vibrancy material, and — the part that matters most —
/// closing when the user clicks away or switches app. The Windows port needed a hand-written
/// `DismissWatcher` (253 lines of Win32 hooks) for exactly that; on macOS `.transient` behaviour
/// is the framework's job, so that file has no counterpart here.
@MainActor
public final class PanelController: NSObject, NSPopoverDelegate {
    private let popover = NSPopover()
    private let panel = PanelView(frame: NSRect(x: 0, y: 0, width: PanelView.width, height: 400))
    private let hosting = NSViewController()

    public var onSelectAccount: ((Int) -> Void)? {
        get { panel.onSelectAccount }
        set { panel.onSelectAccount = newValue }
    }

    /// Called every time the popover needs fresh content — on open, and on each tick while open.
    public var contentProvider: (() -> (label: String, view: QuotaView, others: [OtherAccountRow]))?

    public var isOpen: Bool { popover.isShown }

    public override init() {
        super.init()
        hosting.view = panel
        popover.contentViewController = hosting
        popover.behavior = .transient      // closes on click-away, without a hand-written watcher
        popover.animates = false           // a menu-bar panel should appear instantly
        popover.delegate = self
    }

    public func toggle(relativeTo button: NSStatusBarButton) {
        if popover.isShown { close() } else { show(relativeTo: button) }
    }

    public func show(relativeTo button: NSStatusBarButton) {
        refresh()
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }

    public func close() { popover.performClose(nil) }

    /// Re-renders from the provider. Cheap enough for the 1 Hz tick — the countdowns in the
    /// status box are specified to tick at 1 Hz.
    public func refresh() {
        guard let content = contentProvider?() else { return }
        panel.update(label: content.label, view: content.view, others: content.others)
        let height = panel.intrinsicContentSize.height
        panel.frame = NSRect(x: 0, y: 0, width: PanelView.width, height: height)
        popover.contentSize = NSSize(width: PanelView.width, height: height)
    }
}

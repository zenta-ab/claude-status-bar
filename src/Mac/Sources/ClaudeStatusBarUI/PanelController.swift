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

    /// The last size handed to the popover, so an unchanged tick does not re-lay-it-out.
    private var publishedSize: NSSize = .zero

    public override init() {
        super.init()

        // Auto Layout, not a hand-set frame. Setting `panel.frame` AND `popover.contentSize`
        // on every 1 Hz tick was a fight over who owns the size: whichever ran last won, and the
        // panel visibly jumped between a correct layout and one flush against the left edge.
        // A width constraint means the view is exactly 340 pt whatever the popover does, and
        // `preferredContentSize` is how a view controller is *supposed* to tell a popover how
        // big it wants to be.
        let container = NSView()
        container.translatesAutoresizingMaskIntoConstraints = false
        panel.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(panel)
        NSLayoutConstraint.activate([
            panel.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            panel.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            panel.topAnchor.constraint(equalTo: container.topAnchor),
            panel.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            panel.widthAnchor.constraint(equalToConstant: PanelView.width),
        ])

        hosting.view = container
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

        // Only republish the size when it has actually changed. The countdowns tick every
        // second and almost never change the height; re-asserting the same size on each tick
        // made the popover re-lay-out and jump for no reason.
        let wanted = NSSize(width: PanelView.width, height: panel.intrinsicContentSize.height)
        if abs(wanted.height - publishedSize.height) > 0.5 {
            publishedSize = wanted
            hosting.preferredContentSize = wanted
        }
    }
}

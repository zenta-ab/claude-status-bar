import AppKit
import ClaudeQuotaCore

/// Owns one `NSStatusItem` and keeps its glyph correct.
///
/// **Why this is not just "set button.image once".** The keyline colour is derived from the menu
/// bar's own appearance, read from the status item button. That appearance is **not settled when
/// the item is created**: measured on a system in Dark mode, a button queried immediately after
/// `statusItem(withLength:)` reports `NSAppearanceNameVibrantLight`, and only reports
/// `VibrantDark` once the window server has placed it in the bar. Picking the keyline at creation
/// time therefore ships a black keyline onto a dark menu bar — the one case the keyline exists to
/// prevent — until something else happens to force a redraw.
///
/// It also changes afterwards, whenever the user switches Light/Dark or an Auto schedule fires.
/// So the appearance is *observed*, and the glyph is re-rendered whenever it moves.
@MainActor
public final class StatusItemController {
    public let item: NSStatusItem
    private var params: QuotaIconParams
    private var observation: NSKeyValueObservation?
    private var lastKeyline: RGB?

    /// Lines shown at the top of the menu, above the standard entries. The real panel is M3;
    /// until then this is how the numbers are reachable by clicking rather than only by hovering.
    public var menuHeader: [String] = [] {
        didSet { rebuildMenu() }
    }
    /// Invoked when the user picks "Uppdatera nu".
    public var onRefresh: (() -> Void)?

    /// Invoked on a left click. When set, the item opens this instead of showing its menu —
    /// the panel is the primary surface (docs/panel-v2.md); the menu stays reachable on a
    /// right-click (or a Control-click), which is the platform convention.
    public var onPrimaryClick: ((NSStatusBarButton) -> Void)? {
        didSet { rebuildMenu() }
    }

    public init(params: QuotaIconParams = QuotaIconParams(), length: CGFloat = 24, toolTip: String? = nil) {
        self.params = params
        item = NSStatusBar.system.statusItem(withLength: length)
        item.button?.toolTip = toolTip
        rebuildMenu()

        // Render once immediately so there is never an empty slot in the bar, then again as soon
        // as the real appearance is known.
        redraw()
        observation = item.button?.observe(\.effectiveAppearance, options: [.initial, .new]) { [weak self] _, _ in
            MainActor.assumeIsolated { self?.redraw() }
        }
    }

    deinit {
        observation?.invalidate()
        // Removing the item from the bar cannot be done from a nonisolated deinit (NSStatusItem
        // is not Sendable), so the owner calls `remove()` when it is done with the slot. Dropping
        // the controller without calling it leaves the item until the process exits, which for a
        // menu-bar app that owns its items for its whole lifetime is the normal case.
    }

    /// Takes the item out of the menu bar. Call before dropping the controller if the slot should
    /// disappear while the app keeps running (e.g. an account was removed).
    public func remove() {
        observation?.invalidate()
        observation = nil
        NSStatusBar.system.removeStatusItem(item)
    }

    public func update(_ params: QuotaIconParams) {
        self.params = params
        lastKeyline = nil      // force a redraw even if the appearance has not moved
        redraw()
    }

    /// Re-renders only when something that affects the drawing actually changed. The icon is
    /// redrawn on a 1 s UI tick in the finished app, and pushing an identical `NSImage` every
    /// second is wasted work.
    private func redraw() {
        guard let button = item.button else { return }
        let keyline = MenuBarBackdrop.keylineColour(for: button)
        if let lastKeyline, lastKeyline == keyline, button.image != nil { return }
        lastKeyline = keyline
        button.image = GaugeRenderer.statusItemImage(params, keyline: keyline)
    }

    /// A status item with no menu and no action is dead to the mouse: clicking it does nothing
    /// and there is no way in. Until the M3 panel exists, the menu carries the numbers.
    private func rebuildMenu() {
        let menu = NSMenu()
        for line in menuHeader {
            let entry = NSMenuItem(title: line, action: nil, keyEquivalent: "")
            entry.isEnabled = false
            menu.addItem(entry)
        }
        if !menuHeader.isEmpty { menu.addItem(.separator()) }

        if onRefresh != nil {
            let refresh = NSMenuItem(title: "Uppdatera nu", action: #selector(MenuTarget.refresh(_:)), keyEquivalent: "r")
            refresh.target = menuTarget
            menu.addItem(refresh)
        }
        let quit = NSMenuItem(title: "Avsluta", action: #selector(MenuTarget.quit(_:)), keyEquivalent: "q")
        quit.target = menuTarget
        menu.addItem(quit)

        if onPrimaryClick != nil {
            // With a menu assigned, AppKit swallows the click and shows the menu, so the button
            // never sees an action. Keep the menu off the item and present it manually on a
            // right-click instead.
            item.menu = nil
            contextMenu = menu
            item.button?.target = menuTarget
            item.button?.action = #selector(MenuTarget.click(_:))
            item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        } else {
            item.menu = menu
        }
    }

    private var contextMenu: NSMenu?

    fileprivate func handleClick() {
        guard let button = item.button else { return }
        let isRightClick = NSApp.currentEvent.map {
            $0.type == .rightMouseUp || $0.modifierFlags.contains(.control)
        } ?? false
        if isRightClick, let contextMenu {
            item.menu = contextMenu
            button.performClick(nil)
            item.menu = nil            // put it back, or the next left click shows the menu too
        } else {
            onPrimaryClick?(button)
        }
    }

    private lazy var menuTarget = MenuTarget(owner: self)

    /// AppKit menu actions need an ObjC target; `StatusItemController` stays a plain Swift type.
    @MainActor
    private final class MenuTarget: NSObject {
        weak var owner: StatusItemController?
        init(owner: StatusItemController) { self.owner = owner }
        @objc func refresh(_ sender: Any?) { owner?.onRefresh?() }
        @objc func quit(_ sender: Any?) { NSApplication.shared.terminate(nil) }
        @objc func click(_ sender: Any?) { owner?.handleClick() }
    }

    /// What the glyph is currently being drawn against, for diagnostics and tests.
    public var resolvedAppearanceName: String {
        item.button?.effectiveAppearance.name.rawValue ?? "(no button)"
    }

    public var resolvedKeyline: RGB? {
        guard let button = item.button else { return nil }
        return MenuBarBackdrop.keylineColour(for: button)
    }
}

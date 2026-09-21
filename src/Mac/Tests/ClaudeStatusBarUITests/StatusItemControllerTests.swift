import AppKit
import ClaudeQuotaCore
import Foundation
import Testing

@testable import ClaudeStatusBarUI

/// Regression tests for the appearance bug found while answering "why can't I see the icon?"
/// (2026-09-19).
///
/// On a system in **Dark** mode, a status item button queried immediately after creation reports
/// `NSAppearanceNameVibrantLight`; it only reports `VibrantDark` once the window server has
/// actually placed the item in the menu bar, about a second later. Reading the appearance once, at
/// creation, therefore picks a **black** keyline for a **dark** menu bar — precisely the case the
/// keyline exists to prevent — and nothing would correct it until some unrelated redraw happened.
@Suite("StatusItemController")
@MainActor
struct StatusItemControllerTests {

    @Test("an icon is drawn immediately, so the slot is never empty")
    func drawsImmediately() {
        let controller = StatusItemController(params: sampleParams)
        defer { controller.remove() }
        #expect(controller.item.button?.image != nil)
    }

    /// The core fix: the controller must keep watching, not sample once.
    @Test("the glyph is re-rendered when the effective appearance changes")
    func redrawsOnAppearanceChange() throws {
        let controller = StatusItemController(params: sampleParams)
        defer { controller.remove() }
        let button = try #require(controller.item.button)

        button.appearance = NSAppearance(named: .vibrantDark)
        let onDark = try #require(controller.resolvedKeyline)
        let darkImage = try #require(button.image).tiffRepresentation

        button.appearance = NSAppearance(named: .vibrantLight)
        let onLight = try #require(controller.resolvedKeyline)
        let lightImage = try #require(button.image).tiffRepresentation

        #expect(onDark.relativeLuminance > 0.5, "a dark bar must get a light keyline")
        #expect(onLight.relativeLuminance < 0.5, "a light bar must get a dark keyline")
        #expect(darkImage != lightImage, "the glyph did not change when the appearance did")
    }

    @Test("updating the params re-renders even when the appearance has not moved")
    func redrawsOnParamsChange() throws {
        let controller = StatusItemController(params: sampleParams)
        defer { controller.remove() }
        let button = try #require(controller.item.button)
        button.appearance = NSAppearance(named: .vibrantDark)
        let before = try #require(button.image).tiffRepresentation

        controller.update(QuotaIconParams(weeklyFraction: 1.0, sessionFraction: 0.33,
                                          state: .spent, freshness: .live))
        let after = try #require(button.image).tiffRepresentation
        #expect(before != after)
    }

    @Test("remove() takes the item out of the bar")
    func removeDetachesTheItem() {
        let before = NSStatusBar.system.thickness   // touching the bar; the count is not public
        let controller = StatusItemController(params: sampleParams)
        #expect(controller.item.button != nil)
        controller.remove()
        #expect(before == NSStatusBar.system.thickness)
    }

    private var sampleParams: QuotaIconParams {
        QuotaIconParams(weeklyFraction: 0.39, sessionFraction: 0.45,
                        weeklyForecastFraction: 0.46, sessionForecastFraction: 0.57,
                        state: .safe, freshness: .live)
    }
}

/// The panel must lay out against whatever width it is GIVEN, not a hardcoded one. Drawing
/// against the constant while `NSPopover` sized the view itself is what produced a left margin
/// and a right margin that did not match — and, once the popover and a hand-set frame started
/// fighting over the size on every 1 Hz tick, a panel that visibly jumped between the two.
@Suite("Panel layout")
@MainActor
struct PanelLayoutTests {

    private func panel(width: CGFloat) -> PanelView {
        let view = PanelView(frame: NSRect(x: 0, y: 0, width: width, height: 400))
        view.update(label: "Konto", view: .initial, others: [], now: Date(), timeZone: .current)
        view.frame = NSRect(x: 0, y: 0, width: width, height: view.measuredHeight(for: width))
        return view
    }

    /// Render and find the leftmost and rightmost drawn pixel. Both margins must match, at every
    /// width — that is the property the bug violated.
    private func inkMargins(_ view: PanelView) -> (left: CGFloat, right: CGFloat)? {
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: rep)
        let width = rep.pixelsWide, height = rep.pixelsHigh
        var leftmost = width, rightmost = -1
        for y in 0..<height {
            for x in 0..<width {
                guard let colour = rep.colorAt(x: x, y: y), colour.alphaComponent > 0.05 else { continue }
                leftmost = min(leftmost, x)
                rightmost = max(rightmost, x)
            }
        }
        guard rightmost >= 0 else { return nil }
        let scale = CGFloat(width) / view.bounds.width
        return (CGFloat(leftmost) / scale, (CGFloat(width - 1 - rightmost)) / scale)
    }

    @Test("the left and right margins match, at every width it might be given",
          arguments: [300.0, 340.0, 380.0, 420.0])
    func marginsMatchAtAnyWidth(_ width: Double) throws {
        let view = panel(width: CGFloat(width))
        let margins = try #require(inkMargins(view))
        #expect(abs(margins.left - margins.right) <= 1.5,
                "asymmetric at \(width) pt: left \(margins.left), right \(margins.right)")
        #expect(margins.left >= 8, "content is flush against the left edge at \(width) pt")
    }

    /// Height must follow the width it is measured for, not a cached one — otherwise the popover
    /// is told a size that does not match what gets drawn, and the content is clipped or floats.
    @Test("measured height responds to the width it is given")
    func heightFollowsWidth() {
        let view = panel(width: 340)
        let narrow = view.measuredHeight(for: 240)
        let wide = view.measuredHeight(for: 460)
        #expect(narrow >= wide, "narrower should wrap to at least as tall, got \(narrow) vs \(wide)")
    }

    @Test("measuring leaves no state behind that changes the next drawing")
    func measuringIsPure() {
        let view = panel(width: 340)
        let first = view.measuredHeight(for: 340)
        _ = view.measuredHeight(for: 200)
        #expect(view.measuredHeight(for: 340) == first, "a measurement at another width leaked")
    }
}

/// "Uppdaterad för 0 sekunder sedan — kan vara inaktuell" was two different clocks in one
/// sentence: `lastPollAt` is when we last ASKED, staleness is about when the answer last CHANGED.
/// The contract keeps those apart in `lastChangedAt` and `lastPollAt` precisely so the panel
/// cannot confuse them.
@Suite("Panel freshness line")
@MainActor
struct PanelFreshnessLineTests {

    private func view(freshness: Freshness, changedAgo: TimeInterval?, polledAgo: TimeInterval,
                      now: Date) -> QuotaView {
        QuotaView(session: .empty(.session, QuotaWindows.sessionMinutes),
                  weekly: .empty(.weekly, QuotaWindows.weeklyMinutes),
                  freshness: freshness,
                  lastChangedAt: changedAgo.map { now.addingTimeInterval(-$0) },
                  lastPollAt: now.addingTimeInterval(-polledAgo),
                  pollInterval: 150, error: nil, iconSeverity: .measuring, blockedUntil: nil)
    }

    /// This target has no access to the core suite's helper.
    private func utc(_ text: String) -> Date { QuotaTimeUtil.parseResetsAt(text)! }

    /// The contradiction, reproduced exactly: polled a moment ago, data 23 minutes old, Stale.
    @Test("a fresh poll over stale data reports the DATA's age, not the poll's")
    func reportsDataAgeNotPollAge() {
        let now = utc("2026-09-21T10:00:00+00:00")
        let quotaView = view(freshness: .stale, changedAgo: 23 * 60, polledAgo: 0, now: now)

        // The status box is where the staleness is explained, with the same 23 minutes.
        let box = PanelText.compose(quotaView, now: now, timeZone: TimeZone(secondsFromGMT: 0)!).statusBox
        #expect(box.line3 == "Datan kan vara inaktuell — senast ändrad för 23 min sedan")

        // And the two fields must not be the same value, or the test proves nothing.
        #expect(quotaView.lastChangedAt != quotaView.lastPollAt)
    }

    @Test("an account with nothing trackable still reports an age, not Hämtar… forever")
    func idleAccountStillReportsAnAge() {
        let base = utc("2026-09-21T10:00:00.000000+00:00")
        let model = QuotaModel()
        let idle = UsageSnapshot(sessionUtilization: 0, sessionResetsAt: nil,
                                 weeklyUtilization: 0, weeklyResetsAt: nil,
                                 subscriptionType: "team", sessionIsActive: false,
                                 weeklyIsActive: false, observedAt: base, source: .limitsArray)
        model.ingest(idle, utcNow: base, monoMs: 0)
        let quotaView = model.evaluate(utcNow: base, monoMs: 0)
        // No window was ever accepted, so session.lastAcceptedUtc is nil — the age has to come
        // from the last usable poll instead.
        #expect(quotaView.lastChangedAt != nil, "a healthy idle account reported no data age at all")
        #expect(quotaView.freshness == .live)
    }
}

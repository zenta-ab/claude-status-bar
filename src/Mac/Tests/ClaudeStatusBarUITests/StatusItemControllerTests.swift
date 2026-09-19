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

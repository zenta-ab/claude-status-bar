import AppKit
import ClaudeQuotaCore
import Foundation
import Testing

@testable import ClaudeStatusBarUI

/// The macOS counterpart of `tests/ClaudeStatusBar.Tests/IconContrastTests.cs`.
///
/// The Windows suite asserts ≥ 5:1 against a single known taskbar colour. That test cannot be
/// ported literally, because macOS has no fixed backdrop: the menu bar is translucent over the
/// desktop picture, and the same palette measures 1.02–9.48:1 depending on the wallpaper
/// (docs/mac-port.md §5). So the contract asserted here is the one that replaced it — the glyph's
/// **edge** clears 3:1 against every backdrop the bar can sit on, and the shape, not the hue,
/// carries the signal.
@Suite("Icon contrast")
struct IconContrastTests {

    /// The synthetic set is deliberately hostile and always available, so this suite means the
    /// same thing on a machine with a black wallpaper as on one with a photograph.
    private var backdrops: [MenuBarBackdrop.Sample] { MenuBarBackdrop.syntheticWorstCases }

    @Test("the glyph edge clears the contrast gate on every backdrop")
    @MainActor
    func keylineClearsGateEverywhere() {
        for backdrop in backdrops {
            let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: backdrop.prefersLightForeground)
            let drawn = keyline.composited(over: backdrop.colour, alpha: GaugeRenderer.keylineAlpha)
            let ratio = drawn.contrast(against: backdrop.colour)
            #expect(ratio >= GaugeRenderer.minimumEdgeContrast,
                    "keyline measures \(ratio) on \(backdrop.name), below the \(GaugeRenderer.minimumEdgeContrast) gate")
        }
    }

    /// The finding that forced the keyline in the first place. If someone "simplifies" it away and
    /// goes back to a flat colour glyph, this is the number that shows why it cannot work: the
    /// hue alone drops to roughly 1:1 on a saturated wallpaper, i.e. invisible.
    @Test("hue alone does NOT clear the gate — which is why the keyline exists")
    func hueAloneIsInsufficient() {
        let orange = RGB(0xD2691E)
        let worstHue = QuotaState.allCases
            .map { Palette.color(for: $0).contrast(against: orange) }
            .min() ?? 0
        #expect(worstHue < GaugeRenderer.minimumEdgeContrast,
                "hue contrast unexpectedly cleared the gate; the keyline rationale needs rechecking")
        #expect(worstHue < 1.5, "expected near-invisibility (~1:1), measured \(worstHue)")
    }

    /// Spent must read as QUIETER than an active state, never brighter. An earlier Windows round
    /// lightened the red to chase a contrast target and made Spent the brightest glyph of all —
    /// the opposite of "blocked".
    @Test("Spent renders quieter than Tight and Safe")
    @MainActor
    func spentIsQuieterThanActiveStates() throws {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let spent = try #require(meanLuminance(of: .init(weeklyFraction: 1.0, sessionFraction: 0.33,
                                                         state: .spent, freshness: .live), keyline: keyline))
        let tight = try #require(meanLuminance(of: .init(weeklyFraction: 0.62, sessionFraction: 0.78,
                                                         state: .tight, freshness: .live), keyline: keyline))
        let safe = try #require(meanLuminance(of: .init(weeklyFraction: 0.39, sessionFraction: 0.45,
                                                        state: .safe, freshness: .live), keyline: keyline))
        #expect(spent < tight, "Spent (\(spent)) should read quieter than Tight (\(tight))")
        #expect(spent < safe, "Spent (\(spent)) should read quieter than Safe (\(safe))")
    }

    @Test("Stale is dimmer than the same state when Live")
    @MainActor
    func staleIsDimmerThanLive() throws {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let live = try #require(meanLuminance(of: .init(weeklyFraction: 0.62, sessionFraction: 0.78,
                                                        state: .tight, freshness: .live), keyline: keyline))
        let stale = try #require(meanLuminance(of: .init(weeklyFraction: 0.62, sessionFraction: 0.78,
                                                         state: .tight, freshness: .stale), keyline: keyline))
        #expect(stale < live)
    }

    /// The Spent ring must have no gaps: it is the "blocked" signal, and a dashed ring read as a
    /// lifebuoy rather than a wall when it was tried on Windows.
    @Test("the Spent ring is continuous all the way round")
    @MainActor
    func spentRingHasNoGaps() throws {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let side = 44
        let image = try #require(GaugeRenderer.bitmap(
            .init(weeklyFraction: 1.0, sessionFraction: 0.33, state: .spent, freshness: .live),
            side: side, keyline: keyline))
        let pixels = try #require(rgba(of: image))
        let geometry = GaugeRenderer.Geometry(side: CGFloat(side))

        // Sample the ring's mid-band at every 10 degrees; each sample must be opaque.
        for degrees in stride(from: 0, to: 360, by: 10) {
            let radians = Double(degrees) * .pi / 180
            let x = Int(geometry.center.x + CGFloat(cos(radians)) * geometry.outerRadius)
            let y = Int(geometry.center.y + CGFloat(sin(radians)) * geometry.outerRadius)
            guard x >= 0, y >= 0, x < side, y < side else { continue }
            let alpha = pixels[(y * side + x) * 4 + 3]
            #expect(alpha > 120, "gap in the Spent ring at \(degrees)°, alpha \(alpha)")
        }
    }

    /// Every state has to produce a visibly different glyph, or the icon conveys nothing. This is
    /// what rules out a plain template image, which was measured to make all five look alike.
    @Test("the five states are visually distinct from one another")
    @MainActor
    func statesAreDistinct() throws {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let samples: [(QuotaState, QuotaIconParams)] = [
            (.safe, .init(weeklyFraction: 0.39, sessionFraction: 0.45, state: .safe, freshness: .live)),
            (.tight, .init(weeklyFraction: 0.62, sessionFraction: 0.78, state: .tight, freshness: .live)),
            (.dryEarly, .init(weeklyFraction: 0.71, sessionFraction: 0.56, state: .dryEarly, freshness: .live)),
            (.spent, .init(weeklyFraction: 1.0, sessionFraction: 0.33, state: .spent, freshness: .live)),
            (.measuring, .init(weeklyFraction: 0.39, sessionFraction: 0.09, state: .measuring, freshness: .live)),
        ]
        var rendered: [(QuotaState, [UInt8])] = []
        for (state, params) in samples {
            let image = try #require(GaugeRenderer.bitmap(params, side: 44, keyline: keyline))
            rendered.append((state, try #require(rgba(of: image))))
        }
        for i in rendered.indices {
            for j in (i + 1)..<rendered.count {
                let difference = meanAbsoluteDifference(rendered[i].1, rendered[j].1)
                #expect(difference > 4.0,
                        "\(rendered[i].0) and \(rendered[j].0) differ by only \(difference) — too alike to tell apart")
            }
        }
    }

    @Test("the glyph renders at both shipping sizes without clipping")
    @MainActor
    func rendersAtShippingSizes() throws {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let params = QuotaIconParams(weeklyFraction: 0.62, sessionFraction: 0.78,
                                     weeklyForecastFraction: 0.70, sessionForecastFraction: 0.95,
                                     state: .tight, freshness: .live)
        for side in [Int(GaugeRenderer.statusItemPointSize), Int(GaugeRenderer.statusItemPointSize) * 2] {
            let image = try #require(GaugeRenderer.bitmap(params, side: side, keyline: keyline))
            let pixels = try #require(rgba(of: image))
            // Nothing may touch the outermost pixel ring: that is what clipping looks like.
            for i in 0..<side {
                for (x, y) in [(i, 0), (i, side - 1), (0, i), (side - 1, i)] {
                    #expect(pixels[(y * side + x) * 4 + 3] == 0,
                            "glyph touches the edge at (\(x),\(y)) at side \(side)")
                }
            }
        }
    }

    @Test("the keyline follows the menu bar's appearance, not the app's")
    @MainActor
    func keylineFollowsAppearance() {
        let dark = MenuBarBackdrop.keylineColour(prefersLightForeground: true)
        let light = MenuBarBackdrop.keylineColour(prefersLightForeground: false)
        #expect(dark.relativeLuminance > 0.5, "a dark bar needs a light keyline")
        #expect(light.relativeLuminance < 0.5, "a light bar needs a dark keyline")
    }

    // MARK: - Helpers

    private func rgba(of image: CGImage) -> [UInt8]? {
        let width = image.width, height = image.height
        var data = [UInt8](repeating: 0, count: width * height * 4)
        guard let ctx = CGContext(data: &data, width: width, height: height, bitsPerComponent: 8,
                                  bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return data
    }

    /// Mean luminance of the drawn pixels only, weighted by alpha — "how loud is this glyph".
    @MainActor
    private func meanLuminance(of params: QuotaIconParams, keyline: RGB) -> Double? {
        guard let image = GaugeRenderer.bitmap(params, side: 44, keyline: keyline),
              let pixels = rgba(of: image) else { return nil }
        var total = 0.0, weight = 0.0
        for i in stride(from: 0, to: pixels.count, by: 4) {
            let alpha = Double(pixels[i + 3]) / 255
            guard alpha > 0 else { continue }
            let colour = RGB(r: Double(pixels[i]) / 255, g: Double(pixels[i + 1]) / 255, b: Double(pixels[i + 2]) / 255)
            total += colour.relativeLuminance * alpha
            weight += alpha
        }
        return weight > 0 ? total / weight : nil
    }

    private func meanAbsoluteDifference(_ a: [UInt8], _ b: [UInt8]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return .infinity }
        var total = 0.0
        for i in a.indices { total += abs(Double(a[i]) - Double(b[i])) }
        return total / Double(a.count)
    }
}

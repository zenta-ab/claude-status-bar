import AppKit
import ClaudeQuotaCore
import CoreGraphics

/// Draws the menu-bar glyph. Geometry is ported from `src/Windows/Icons/GaugeRenderer.cs` so the
/// two platforms cannot drift into different-looking icons: outer ring = weekly "all models",
/// a genuinely transparent gap, inner pie = the 5 h session, and a forecast wedge that protrudes
/// past each consumed arc.
///
/// **What differs from Windows, and why** (docs/mac-port.md §5, "The menu bar"). The Windows icon
/// is designed against a fixed `#202020` taskbar and targets ≥ 5:1 against it. The macOS menu bar
/// is translucent over the desktop picture, so there is no fixed backdrop at all: measured against
/// the *shipped default wallpaper*, the flat palette collapses to 1.02–3.27:1, and the DryEarly
/// red is literally invisible at 1.02:1.
///
/// Three variants were rendered over eight backdrops before choosing. A template (monochrome)
/// icon is always legible but makes all five states look identical — it destroys the one thing the
/// icon exists to convey. The flat colour icon fails outright. So the glyph keeps its hue *and*
/// carries its own contrast: a dark contact halo under the ring, and a 1 pt keyline in the menu
/// bar's own `labelColor`, taken from the status item button's `effectiveAppearance` (which
/// reports `VibrantDark`/`VibrantLight` — the macOS equivalent of the Windows `taskbarDark` flag).
/// The glyph *edge* then measures ≥ 3.6:1 against every backdrop tested.
///
/// The ring's filled fraction, not its hue, is the primary signal: someone who cannot separate the
/// colours from their wallpaper still reads the icon.
public enum GaugeRenderer {

    /// The macOS icon contract, replacing the Windows "≥ 5:1 against `#202020`". There is no fixed
    /// backdrop here, so the promise is about the glyph's EDGE rather than its hue: 3:1 is the
    /// standard minimum for a non-text UI component, and it is the number `IconContrastTests` and
    /// the contact sheet both gate on.
    public static let minimumEdgeContrast: Double = 3.0


    /// `band` and the gap are the Windows constants, unchanged. `outerRadius` is **not**: the
    /// Windows glyph has nothing outside the ring, while this one carries a halo and a keyline
    /// there, and the original radius left no room for them. They were being clipped by the
    /// bitmap edge — measured at alpha 253 on the outermost pixel row, i.e. a visibly flat-sided
    /// ring. `rendersAtShippingSizes` is the test that caught it and now holds the line.
    public struct Geometry {
        public let band: CGFloat
        public let outerRadius: CGFloat
        public let pieRadius: CGFloat
        public let center: CGPoint
        public let keylineWidth: CGFloat
        /// Stroke width of the dark contact halo. It is centred on the ring, so it reaches
        /// `keylineWidth / 2` beyond the band on each side.
        public let haloWidth: CGFloat

        public init(side: CGFloat) {
            band = side * 0.155
            // 1 pt, scaled by the backing factor, never thinner than a device pixel.
            keylineWidth = max(side / 22.0, 1.0)
            haloWidth = band + keylineWidth
            // Everything drawn must fit inside `side`, with one device pixel of margin so the
            // antialiased edge is never cut. The outermost ink is the halo (and the keyline,
            // which reaches the same radius).
            let margin: CGFloat = 1
            outerRadius = side * 0.5 - margin - band * 0.5 - keylineWidth * 0.5
            let gap = band * 0.42
            pieRadius = outerRadius - band * 0.5 - gap
            center = CGPoint(x: side / 2, y: side / 2)
        }

        /// The radius of the outermost ink, for tests and for anyone reasoning about the fit.
        public var outerInkRadius: CGFloat { outerRadius + band * 0.5 + keylineWidth * 0.5 }
    }

    /// Renders at `side` **pixels**. Callers pass 22 for 1x and 44 for 2x and wrap the results in
    /// one `NSImage` so AppKit picks the right representation per display.
    public static func render(
        _ params: QuotaIconParams,
        side: CGFloat,
        keyline: RGB,
        into context: CGContext
    ) {
        let geometry = Geometry(side: side)
        let colour = Palette.color(for: params.state)

        var alpha = 1.0
        if params.state == .spent { alpha *= Palette.spentDimAlpha }
        if params.freshness == .stale { alpha *= Palette.staleAlpha }

        context.saveGState()
        defer { context.restoreGState() }
        context.setAlpha(alpha)

        // Unknown: a grey outline plus an exclamation mark, so it reads as "can't read the quota"
        // rather than "still loading". An empty ring was indistinguishable from a genuine 0 %.
        if params.freshness == .unknown {
            drawUnknown(geometry, keyline: keyline, into: context)
            return
        }

        drawRing(params, geometry, colour: colour, keyline: keyline, into: context)
        punchGap(geometry, into: context)
        drawPie(params, geometry, colour: colour, keyline: keyline, into: context)

        if params.freshness == .stale {
            drawStaleDot(geometry, keyline: keyline, into: context)
        }
    }

    // MARK: - Pieces

    private static func drawRing(
        _ params: QuotaIconParams, _ g: Geometry, colour: RGB, keyline: RGB, into ctx: CGContext
    ) {
        // The contact halo: what gives the glyph an edge on a mid-grey or saturated wallpaper,
        // where the hue itself measures barely above 1:1.
        ctx.setStrokeColor(CGColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.55))
        ctx.setLineWidth(g.haloWidth)
        ctx.addArc(center: g.center, radius: g.outerRadius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()

        // The unfilled track.
        ctx.setStrokeColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: params.state == .spent ? 40.0 / 255 : 70.0 / 255))
        ctx.setLineWidth(g.band)
        ctx.addArc(center: g.center, radius: g.outerRadius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()

        if params.state == .spent {
            // A full, continuous ring: "blocked", not "loading". Only the pie moves in this state.
            ctx.setStrokeColor(colour.cgColor)
            ctx.setLineWidth(g.band)
            ctx.addArc(center: g.center, radius: g.outerRadius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
            ctx.strokePath()
        } else {
            // The forecast wedge protrudes radially past the consumed arc. Below 20 px the band is
            // ~2.5 px and it is not legible, so it is dropped rather than drawn as mush.
            if side(of: g) >= 20, params.weeklyForecastFraction > params.weeklyFraction {
                let wide = g.band * 1.55
                ctx.setStrokeColor(colour.cgColor(alpha: 96.0 / 255))
                ctx.setLineWidth(wide)
                strokeArc(ctx, g, from: params.weeklyFraction, to: params.weeklyForecastFraction, radius: g.outerRadius)
            }
            if params.weeklyFraction > 0 {
                ctx.setStrokeColor(colour.cgColor)
                ctx.setLineWidth(g.band)
                strokeArc(ctx, g, from: 0, to: params.weeklyFraction, radius: g.outerRadius)
            }
        }

        // The keyline, on the ring's outer edge, in the menu bar's own label colour. Drawn at
        // FULL opacity: this single 1 pt line is what the ">= 3:1 against any backdrop" contract
        // actually rests on, and at 0.85 the worst measured backdrop came in at 3.05:1 -- over the
        // line, but with no margin for a wallpaper slightly worse than the worst one tested. At
        // full opacity the same backdrop measures 3.63:1. Interior strokes stay at 0.85, where
        // they are separators rather than the glyph's edge.
        ctx.setStrokeColor(keyline.cgColor(alpha: keylineAlpha))
        ctx.setLineWidth(g.keylineWidth)
        ctx.addArc(center: g.center, radius: g.outerRadius + g.band / 2,
                   startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()
    }

    /// A genuinely transparent annulus, so the menu bar shows through between ring and pie. A
    /// filled "background" colour would be wrong on a translucent bar.
    private static func punchGap(_ g: Geometry, into ctx: CGContext) {
        let gap = g.band * 0.42
        let radius = g.pieRadius + gap
        ctx.setBlendMode(.clear)
        ctx.fillEllipse(in: CGRect(x: g.center.x - radius, y: g.center.y - radius,
                                   width: radius * 2, height: radius * 2))
        ctx.setBlendMode(.normal)
    }

    private static func drawPie(
        _ params: QuotaIconParams, _ g: Geometry, colour: RGB, keyline: RGB, into ctx: CGContext
    ) {
        // In the spent state the pie is a countdown to the reset, not usage.
        fillSector(ctx, g, from: 0, to: params.sessionFraction, colour: colour, alpha: 1, keyline: keyline)

        if params.state != .spent, params.sessionForecastFraction > params.sessionFraction {
            // Drawn at every size, unlike the ring wedge: at the real menu-bar size this is the
            // only forecast the user can actually see.
            fillSector(ctx, g, from: params.sessionFraction, to: params.sessionForecastFraction,
                       colour: colour, alpha: 112.0 / 255, keyline: keyline)
        }
    }

    private static func drawStaleDot(_ g: Geometry, keyline: RGB, into ctx: CGContext) {
        let radius = max(g.keylineWidth * 1.2, g.band * 0.30)
        let dot = CGRect(x: g.center.x + g.outerRadius - radius,
                         y: g.center.y - g.outerRadius - radius,
                         width: radius * 2, height: radius * 2)
        ctx.setFillColor(keyline.cgColor(alpha: 0.95))
        ctx.fillEllipse(in: dot)
    }

    private static func drawUnknown(_ g: Geometry, keyline: RGB, into ctx: CGContext) {
        ctx.setStrokeColor(Palette.dead.cgColor(alpha: 0.75))
        ctx.setLineWidth(g.band * 0.45)
        ctx.addArc(center: g.center, radius: g.outerRadius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()
        ctx.setStrokeColor(keyline.cgColor(alpha: keylineAlpha))
        ctx.setLineWidth(g.keylineWidth)
        ctx.addArc(center: g.center, radius: g.outerRadius + g.band * 0.22,
                   startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()

        // "!" — pixel-snapped and drawn at output resolution. At 22 px it is a 2 px bar over a
        // dot, and the antialiasing the rest of this pipeline relies on would blur it away.
        let side = self.side(of: g)
        let barWidth = max(1, (side * 0.09).rounded())
        let barHeight = (side * 0.30).rounded()
        let x = (g.center.x - barWidth / 2).rounded()
        ctx.setFillColor(keyline.cgColor(alpha: 0.95))
        ctx.fill(CGRect(x: x, y: (g.center.y - barHeight * 0.30).rounded(), width: barWidth, height: barHeight))
        ctx.fill(CGRect(x: x, y: (g.center.y - barHeight * 0.75).rounded(), width: barWidth, height: barWidth))
    }

    // MARK: - Primitives

    /// Clockwise from 12 o'clock, which is how both platforms read a gauge.
    private static func strokeArc(
        _ ctx: CGContext, _ g: Geometry, from: Double, to: Double, radius: CGFloat
    ) {
        let start = CGFloat.pi / 2 - CGFloat(from) * 2 * .pi
        let end = CGFloat.pi / 2 - CGFloat(min(1, to)) * 2 * .pi
        ctx.addArc(center: g.center, radius: radius, startAngle: start, endAngle: end, clockwise: true)
        ctx.strokePath()
    }

    private static func fillSector(
        _ ctx: CGContext, _ g: Geometry, from: Double, to: Double,
        colour: RGB, alpha: Double, keyline: RGB
    ) {
        guard to > from, to > 0 else { return }
        let start = CGFloat.pi / 2 - CGFloat(from) * 2 * .pi
        let end = CGFloat.pi / 2 - CGFloat(min(1, to)) * 2 * .pi
        ctx.beginPath()
        ctx.move(to: g.center)
        ctx.addArc(center: g.center, radius: g.pieRadius, startAngle: start, endAngle: end, clockwise: true)
        ctx.closePath()
        ctx.setFillColor(colour.cgColor(alpha: alpha))
        ctx.setStrokeColor(keyline.cgColor(alpha: interiorStrokeAlpha))
        ctx.setLineWidth(g.keylineWidth)
        ctx.drawPath(using: .fillStroke)
    }

    private static func side(of g: Geometry) -> CGFloat { g.band / 0.155 }

    // MARK: - Bitmaps

    public static func bitmap(_ params: QuotaIconParams, side: Int, keyline: RGB) -> CGImage? {
        guard let ctx = CGContext(
            data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setShouldAntialias(true)
        render(params, side: CGFloat(side), keyline: keyline, into: ctx)
        return ctx.makeImage()
    }

    /// The opacity of the outer keyline — the glyph's edge, and the single thing the
    /// ">= 3:1 against any backdrop" contract rests on. Exposed so the contact sheet and
    /// `IconContrastTests` measure what is actually drawn rather than a copy of the number.
    public static let keylineAlpha: Double = 1.0
    /// Interior strokes (the pie sectors) are separators, not the glyph edge.
    public static let interiorStrokeAlpha: Double = 0.85

    /// The point size the status item actually ships at, inside a 22 pt bar. One constant so the
    /// contact sheet and the app can never test different sizes from the ones that ship.
    public static let statusItemPointSize: CGFloat = 18

    /// One `NSImage` carrying both a 1x and a 2x representation, sized in points for a 22 pt bar.
    /// `isTemplate` stays false: the whole point is that the system must not recolour this.
    @MainActor
    public static func statusItemImage(_ params: QuotaIconParams, pointSize: CGFloat = statusItemPointSize, keyline: RGB) -> NSImage {
        let image = NSImage(size: NSSize(width: pointSize, height: pointSize))
        for scale in [1, 2] {
            let side = Int(pointSize) * scale
            guard let cg = bitmap(params, side: side, keyline: keyline) else { continue }
            let rep = NSBitmapImageRep(cgImage: cg)
            rep.size = NSSize(width: pointSize, height: pointSize)
            image.addRepresentation(rep)
        }
        image.isTemplate = false
        return image
    }
}

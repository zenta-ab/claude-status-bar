import AppKit

/// What the menu bar actually sits on, measured rather than assumed.
///
/// The Windows icon has a fixed `#202020` taskbar to target. macOS has nothing of the sort: the
/// menu bar is translucent over the desktop picture, so the backdrop is whatever the user's
/// wallpaper happens to be under those 22 points. Phase 1 (docs/mac-port.md §5) established two
/// ways to measure it that need no Screen Recording permission, and both are implemented here.
public enum MenuBarBackdrop {

    public struct Sample: Sendable {
        public let name: String
        public let colour: RGB
        /// True when a light foreground (white keyline) is the right choice for this backdrop.
        public let prefersLightForeground: Bool
    }

    /// The opaque colour `NSVisualEffectView` falls back to — which is exactly what the menu bar
    /// becomes under "Reduce transparency". Read from AppKit rather than hardcoded, so it tracks
    /// whatever a future macOS decides these materials are.
    @MainActor
    public static func opaqueMaterialColour(
        material: NSVisualEffectView.Material = .menu,
        appearance appearanceName: NSAppearance.Name
    ) -> RGB? {
        let view = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: 40, height: 22))
        view.material = material
        view.blendingMode = .behindWindow
        view.state = .active
        view.appearance = NSAppearance(named: appearanceName)
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let colour = rep.colorAt(x: 20, y: 11)?.usingColorSpace(.sRGB) else { return nil }
        return RGB(r: colour.redComponent, g: colour.greenComponent, b: colour.blueComponent)
    }

    /// Samples the user's real desktop picture across the strip the menu bar covers, in columns
    /// the width of one status item. The extremes are what matter: an icon sits in *one* column,
    /// so the darkest and brightest columns are the two cases it has to survive.
    @MainActor
    public static func wallpaperColumns(for screen: NSScreen? = NSScreen.main) -> [RGB] {
        guard let screen,
              let url = NSWorkspace.shared.desktopImageURL(for: screen),
              let image = NSImage(contentsOf: url),
              let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return [] }

        let width = cg.width, height = cg.height
        let stripFraction = 22.0 / Double(screen.frame.height)
        let stripPixels = max(1, Int(Double(height) * stripFraction))
        var data = [UInt8](repeating: 0, count: width * stripPixels * 4)
        guard let ctx = CGContext(
            data: &data, width: width, height: stripPixels, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return [] }
        // CoreGraphics origin is bottom-left, so the menu-bar strip is the TOP of the image.
        ctx.draw(cg, in: CGRect(x: 0, y: -(CGFloat(height) - CGFloat(stripPixels)),
                                width: CGFloat(width), height: CGFloat(height)))

        let columnWidth = max(1, Int(Double(width) * 22.0 / Double(screen.frame.width)))
        var columns: [RGB] = []
        var x = 0
        while x + columnWidth <= width {
            var r = 0.0, g = 0.0, b = 0.0, n = 0.0
            for xx in x..<(x + columnWidth) {
                for yy in 0..<stripPixels {
                    let o = (yy * width + xx) * 4
                    r += Double(data[o]) / 255; g += Double(data[o + 1]) / 255; b += Double(data[o + 2]) / 255
                    n += 1
                }
            }
            columns.append(RGB(r: r / n, g: g / n, b: b / n))
            x += columnWidth
        }
        return columns
    }

    /// The backdrops the icon has to survive on this machine, right now: the real wallpaper's
    /// extremes plus both Reduce-Transparency materials. Falls back to a fixed synthetic set when
    /// there is no screen (headless CI), so the contrast test still means something there.
    @MainActor
    public static func measuredBackdrops(for screen: NSScreen? = NSScreen.main) -> [Sample] {
        var samples: [Sample] = []

        let columns = wallpaperColumns(for: screen)
        if let darkest = columns.min(by: { $0.relativeLuminance < $1.relativeLuminance }),
           let brightest = columns.max(by: { $0.relativeLuminance < $1.relativeLuminance }) {
            samples.append(Sample(name: "wallpaper, darkest column", colour: darkest,
                                  prefersLightForeground: darkest.relativeLuminance < 0.35))
            samples.append(Sample(name: "wallpaper, brightest column", colour: brightest,
                                  prefersLightForeground: brightest.relativeLuminance < 0.35))
        }

        if let dark = opaqueMaterialColour(appearance: .vibrantDark) {
            samples.append(Sample(name: "reduce transparency, dark", colour: dark, prefersLightForeground: true))
        }
        if let light = opaqueMaterialColour(appearance: .vibrantLight) {
            samples.append(Sample(name: "reduce transparency, light", colour: light, prefersLightForeground: false))
        }

        samples.append(contentsOf: syntheticWorstCases)
        return samples
    }

    /// Deliberately hostile backdrops kept alongside the measured ones. The user's wallpaper today
    /// is not the contract -- the icon has to survive the wallpaper they pick tomorrow, and a
    /// mid-grey or a saturated colour is where a flat palette collapses.
    public static let syntheticWorstCases: [Sample] = [
        Sample(name: "black", colour: RGB(0x000000), prefersLightForeground: true),
        Sample(name: "mid grey", colour: RGB(0x808080), prefersLightForeground: true),
        Sample(name: "white", colour: RGB(0xFFFFFF), prefersLightForeground: false),
        Sample(name: "saturated orange", colour: RGB(0xD2691E), prefersLightForeground: true),
        Sample(name: "saturated green", colour: RGB(0x2E7D32), prefersLightForeground: true),
        Sample(name: "saturated blue", colour: RGB(0x1B3A6B), prefersLightForeground: true),
    ]

    /// The colour the system tints a template image to, and therefore the right keyline colour:
    /// white at 90 % on a dark bar, black at 70 % on a light one. Resolved from AppKit rather than
    /// hardcoded. In the app this comes from the status item **button's** `effectiveAppearance`,
    /// which reports the MENU BAR's appearance (`VibrantDark`/`VibrantLight`) -- not
    /// `NSApp.effectiveAppearance`, which is the app's own and can differ.
    @MainActor
    public static func keylineColour(prefersLightForeground: Bool) -> RGB {
        let name: NSAppearance.Name = prefersLightForeground ? .vibrantDark : .vibrantLight
        var resolved = prefersLightForeground ? RGB(0xFFFFFF) : RGB(0x000000)
        NSAppearance(named: name)?.performAsCurrentDrawingAppearance {
            if let label = NSColor.labelColor.usingColorSpace(.sRGB) {
                resolved = RGB(r: label.redComponent, g: label.greenComponent, b: label.blueComponent)
            }
        }
        return resolved
    }

    /// The keyline for a live status item, read from the button that will actually draw it.
    @MainActor
    public static func keylineColour(for view: NSView) -> RGB {
        let isDark = view.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua, .vibrantLight, .vibrantDark])
            .map { $0 == .darkAqua || $0 == .vibrantDark } ?? true
        return keylineColour(prefersLightForeground: isDark)
    }
}

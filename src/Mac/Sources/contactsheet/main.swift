import AppKit
import ClaudeQuotaCore
import ClaudeStatusBarUI
import Foundation

// M1's acceptance gate (docs/mac-port.md): every state, at 1x and 2x, over every backdrop the menu
// bar can actually sit on -- including THIS machine's real wallpaper, read from
// NSWorkspace.desktopImageURL, and the opaque material that "Reduce transparency" produces.
//
// It prints the measured contrast table and writes a PNG contact sheet.
//
//   contactsheet [out.png]     write the sheet and print the table
//   contactsheet --menubar     also put a live NSStatusItem in the menu bar to eyeball
setvbuf(stdout, nil, _IONBF, 0)

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let arguments = Array(CommandLine.arguments.dropFirst())
let showMenuBar = arguments.contains("--menubar")
let outputPath = arguments.first { !$0.hasPrefix("--") }
    ?? FileManager.default.currentDirectoryPath + "/contact-sheet.png"

/// The five states, with fractions chosen so each one's shape is distinguishable at 22 px --
/// which is the point: the filled fraction, not the hue, is the primary signal.
/// 18 pt is what ships (`GaugeRenderer.statusItemPointSize`), so 18 px is 1x and 36 px is 2x.
/// 44 px is kept as a magnified reference for judging the drawing itself, not for the gate.
let renderSizes = [Int(GaugeRenderer.statusItemPointSize),
                   Int(GaugeRenderer.statusItemPointSize) * 2,
                   44]

let states: [(name: String, params: QuotaIconParams)] = [
    ("Safe", QuotaIconParams(weeklyFraction: 0.39, sessionFraction: 0.45,
                             weeklyForecastFraction: 0.46, sessionForecastFraction: 0.57,
                             state: .safe, freshness: .live)),
    ("Tight", QuotaIconParams(weeklyFraction: 0.62, sessionFraction: 0.78,
                              weeklyForecastFraction: 0.70, sessionForecastFraction: 0.95,
                              state: .tight, freshness: .live)),
    ("DryEarly", QuotaIconParams(weeklyFraction: 0.71, sessionFraction: 0.56,
                                 weeklyForecastFraction: 0.88, sessionForecastFraction: 1.0,
                                 state: .dryEarly, freshness: .live)),
    ("Spent", QuotaIconParams(weeklyFraction: 1.0, sessionFraction: 0.33,
                              state: .spent, freshness: .live)),
    ("Measuring", QuotaIconParams(weeklyFraction: 0.39, sessionFraction: 0.09,
                                  state: .measuring, freshness: .live)),
    ("Stale", QuotaIconParams(weeklyFraction: 0.62, sessionFraction: 0.78,
                              weeklyForecastFraction: 0.70, sessionForecastFraction: 0.95,
                              state: .tight, freshness: .stale)),
    ("Unknown", QuotaIconParams(state: .measuring, freshness: .unknown)),
]

/// Left-pads to a fixed width. `String(format:)` with `%s`/`%@` and a Swift String is a trap --
/// it takes a C-string pointer for `%s` and an object for `%@`, and mixing them crashes.
func pad(_ text: String, _ width: Int) -> String {
    text.count >= width ? text : text + String(repeating: " ", count: width - text.count)
}

@MainActor
func body() {
    let backdrops = MenuBarBackdrop.measuredBackdrops()

    print("=== backdrops measured on this machine ===")
    for backdrop in backdrops {
        let hex = String(format: "#%02X%02X%02X",
                         Int(backdrop.colour.r * 255), Int(backdrop.colour.g * 255), Int(backdrop.colour.b * 255))
        let lum = String(format: "%.4f", backdrop.colour.relativeLuminance)
        print("  \(pad(backdrop.name, 28))  \(hex)  relLum \(lum)  keyline "
              + (backdrop.prefersLightForeground ? "white" : "black"))
    }

    // The contract that replaced Windows' "≥ 5:1 against #202020": the glyph EDGE must clear 3:1
    // against every backdrop, because the hue cannot be relied on when the backdrop is arbitrary.
    print("\n=== keyline (glyph edge) contrast — the M1 gate is >= \(GaugeRenderer.minimumEdgeContrast) everywhere ===")
    var worst = (ratio: Double.infinity, backdrop: "")
    for backdrop in backdrops {
        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: backdrop.prefersLightForeground)
        // Measure what is actually drawn, not a copy of the number: the renderer owns this.
        let effective = keyline.composited(over: backdrop.colour, alpha: GaugeRenderer.keylineAlpha)
        let ratio = effective.contrast(against: backdrop.colour)
        if ratio < worst.ratio { worst = (ratio, backdrop.name) }
        let verdict = ratio >= GaugeRenderer.minimumEdgeContrast ? "ok" : "FAILS"
        print("  \(pad(backdrop.name, 28))  \(String(format: "%6.2f", ratio))  \(verdict)")
    }
    print("\nworst case: \(String(format: "%.2f", worst.ratio)):1 on \(worst.backdrop)")

    print("\n=== hue contrast, for reference — this is what CANNOT be relied on ===")
    print("  " + pad("state", 12) + backdrops.prefix(6).map { pad(String($0.name.prefix(10)), 11) }.joined())
    for state in QuotaState.allCases {
        var line = "  " + pad(String(describing: state), 12)
        for backdrop in backdrops.prefix(6) {
            let colour = Palette.color(for: state)
            line += pad(String(format: "%.2f", colour.contrast(against: backdrop.colour)), 11)
        }
        print(line)
    }

    writeSheet(backdrops: backdrops, to: URL(fileURLWithPath: outputPath))
    print("\nwrote \(outputPath)")

    if showMenuBar {
        installStatusItems()
        // Report only AFTER the window server has placed the items: both the frame and the
        // effective appearance are meaningless until then.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { MainActor.assumeIsolated { reportStatusItems() } }
        if let screen = NSScreen.main {
            print("  screen is \(Int(screen.frame.width))x\(Int(screen.frame.height)); "
                  + "the menu bar is the top \(Int(NSStatusBar.system.thickness)) pt")
        }
        print("\nLook at the RIGHT-HAND side of the menu bar, left of the clock.")
        print("Hover for a tooltip naming each state. Ctrl-C to quit.")
    } else {
        exit(worst.ratio >= GaugeRenderer.minimumEdgeContrast ? 0 : 1)
    }
}

@MainActor
func writeSheet(backdrops: [MenuBarBackdrop.Sample], to url: URL) {
    let cell: CGFloat = 64, labelHeight: CGFloat = 16, gutter: CGFloat = 190
    let sheetWidth = Int(gutter + CGFloat(states.count) * CGFloat(renderSizes.reduce(0) { $0 + $1 + 6 } + 8) + 40)
    let sheetHeight = Int((cell + 6) * CGFloat(backdrops.count) + labelHeight + 24)
    guard let sheet = CGContext(
        data: nil, width: sheetWidth, height: sheetHeight, bitsPerComponent: 8,
        bytesPerRow: sheetWidth * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }

    sheet.setFillColor(CGColor(srgbRed: 0.09, green: 0.09, blue: 0.10, alpha: 1))
    sheet.fill(CGRect(x: 0, y: 0, width: sheetWidth, height: sheetHeight))

    func label(_ text: String, _ x: CGFloat, _ y: CGFloat, size: CGFloat = 9, colour: NSColor = .white) {
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: sheet, flipped: false)
        NSAttributedString(string: text, attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: size, weight: .regular),
            .foregroundColor: colour,
        ]).draw(at: NSPoint(x: x, y: y))
        NSGraphicsContext.restoreGraphicsState()
    }

    for (index, backdrop) in backdrops.enumerated() {
        let rowY = CGFloat(sheetHeight) - (cell + 6) * CGFloat(index + 1) - labelHeight
        sheet.setFillColor(backdrop.colour.cgColor)
        sheet.fill(CGRect(x: gutter - 8, y: rowY, width: CGFloat(sheetWidth) - gutter, height: cell))
        label(backdrop.name, 6, rowY + cell / 2 - 4)

        let keyline = MenuBarBackdrop.keylineColour(prefersLightForeground: backdrop.prefersLightForeground)
        var x = gutter
        for (name, params) in states {
            for side in renderSizes {
                if let image = GaugeRenderer.bitmap(params, side: side, keyline: keyline) {
                    sheet.draw(image, in: CGRect(x: x, y: rowY + (cell - CGFloat(side)) / 2,
                                                 width: CGFloat(side), height: CGFloat(side)))
                }
                if index == 0 {
                    label("\(name.prefix(5))\(side)", x - 2, CGFloat(sheetHeight) - 13, size: 7)
                }
                x += CGFloat(side) + 6
            }
            x += 8
        }
    }

    if let image = sheet.makeImage() {
        try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?.write(to: url)
    }
}

@MainActor
enum StatusItems {
    /// Held so ARC does not drop the controllers the moment installStatusItems returns, which
    /// would make the items vanish from the menu bar immediately.
    static var live: [StatusItemController] = []
}

@MainActor
func reportStatusItems() {
    print("\n=== live status items ===")
    for (index, controller) in StatusItems.live.enumerated() {
        let name = states[index].name
        guard let window = controller.item.button?.window else {
            print("  \(pad(name, 10)) NO WINDOW — the window server refused it")
            continue
        }
        let f = window.frame
        let keyline = controller.resolvedKeyline.map { $0.relativeLuminance > 0.5 ? "white" : "black" } ?? "?"
        print("  \(pad(name, 10)) x \(Int(f.origin.x))…\(Int(f.origin.x + f.width))  y \(Int(f.origin.y))"
              + "  \(pad(controller.resolvedAppearanceName, 30))  keyline \(keyline)")
    }
    if let screen = NSScreen.main {
        print("  screen \(Int(screen.frame.width))x\(Int(screen.frame.height)), "
              + "menu bar = the top \(Int(NSStatusBar.system.thickness)) pt "
              + "(so y ≈ \(Int(screen.frame.height - NSStatusBar.system.thickness))…\(Int(screen.frame.height)))")
    }
    print("\nLook at the RIGHT-HAND side of the menu bar, left of the clock.")
    print("Hover for a tooltip naming each state. Ctrl-C to quit.")
}

@MainActor
func installStatusItems() {
    for (name, params) in states {
        // StatusItemController observes the appearance rather than reading it once: a button
        // queried at creation time reports the WRONG appearance (see its doc comment).
        StatusItems.live.append(StatusItemController(params: params, toolTip: name))
    }
}

DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { MainActor.assumeIsolated { body() } }
app.run()

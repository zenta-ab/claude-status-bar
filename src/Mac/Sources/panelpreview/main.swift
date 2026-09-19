import AppKit
import ClaudeQuotaCore
import ClaudeStatusBarUI
import Foundation

// Renders the panel (docs/panel-v2.md) offscreen for every state worth looking at, so the layout
// can be judged without clicking through a live menu bar — and so a regression in the measuring
// or drawing code shows up as a picture, which no string test would catch.
//
//   panelpreview [out.png]
setvbuf(stdout, nil, _IONBF, 0)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let outputPath = CommandLine.arguments.dropFirst().first
    ?? FileManager.default.currentDirectoryPath + "/panel-preview.png"

let now = QuotaTimeUtil.parseResetsAt("2026-09-11T10:00:00+00:00")!   // 11:00 local (+01:00)
let tz = TimeZone(secondsFromGMT: 3600)!

func window(_ kind: WindowKind, used: Double?, resetsIn: TimeInterval, state: QuotaState,
            projected: Double? = nil, depletesIn: TimeInterval? = nil,
            shortfall: Double = 0, reason: String? = nil) -> WindowView {
    WindowView(kind: kind,
               windowMinutes: kind == .session ? QuotaWindows.sessionMinutes : QuotaWindows.weeklyMinutes,
               usedPct: used, resetsAt: now.addingTimeInterval(resetsIn), state: state,
               ratePctPerMin: state == .measuring ? nil : 0.3,
               paceMultiple: state == .measuring ? nil : 1.2,
               projectedPctAtReset: projected,
               depletesAt: depletesIn.map { now.addingTimeInterval($0) },
               shortfallMinutes: shortfall, measuringReason: reason)
}

func view(_ session: WindowView, _ weekly: WindowView, freshness: Freshness = .live,
          blockedUntil: Date? = nil) -> QuotaView {
    QuotaView(session: session, weekly: weekly, freshness: freshness,
              lastChangedAt: now.addingTimeInterval(-90), lastPollAt: now.addingTimeInterval(-20),
              pollInterval: 150, error: nil,
              iconSeverity: QuotaState(rawValue: max(session.state.rawValue, weekly.state.rawValue))!,
              blockedUntil: blockedUntil)
}

let cases: [(String, QuotaView, [OtherAccountRow])] = [
    ("Safe", view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                  window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)), []),
    ("Tight", view(window(.session, used: 78, resetsIn: 61 * 60, state: .tight, projected: 95),
                   window(.weekly, used: 40, resetsIn: 5 * 86_400, state: .safe, projected: 55)), []),
    ("DryEarly", view(window(.session, used: 56, resetsIn: 168 * 60, state: .dryEarly,
                             projected: 127, depletesIn: 104 * 60, shortfall: 64),
                      window(.weekly, used: 62, resetsIn: 4 * 86_400, state: .tight, projected: 88)), []),
    ("Spent", view(window(.session, used: 100, resetsIn: 101 * 60, state: .spent),
                   window(.weekly, used: 71, resetsIn: 4 * 86_400, state: .safe, projected: 80),
                   blockedUntil: now.addingTimeInterval(101 * 60)), []),
    ("Measuring", view(window(.session, used: 3, resetsIn: 292 * 60, state: .measuring,
                              reason: "För lite förbrukning ännu…"),
                       window(.weekly, used: 11, resetsIn: 6 * 86_400, state: .measuring,
                              reason: "För tidigt i veckan — väntar på ett helt dygn")), []),
    ("Stale", view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                   window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34),
                   freshness: .stale), []),
    ("Unknown", view(window(.session, used: nil, resetsIn: 61 * 60, state: .measuring, reason: "Hämtar…"),
                     window(.weekly, used: nil, resetsIn: 5 * 86_400, state: .measuring, reason: "Hämtar…"),
                     freshness: .unknown), []),
    ("AwaitingReset", view(window(.session, used: 60, resetsIn: -120, state: .measuring,
                                  reason: "Nytt fönster väntas"),
                           window(.weekly, used: 40, resetsIn: 5 * 86_400, state: .safe, projected: 55)), []),
    ("Two accounts", view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                          window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)),
     [OtherAccountRow(accountIndex: 1, label: "Max", line: "tajt — räcker precis", role: .tight)]),
]

@MainActor
func render() {
    var images: [(String, NSImage)] = []
    for (name, quotaView, others) in cases {
        let panel = PanelView(frame: NSRect(x: 0, y: 0, width: PanelView.width, height: 400))
        // Placeholder labels only. This repo is public: a real organisation name must never be
        // baked into sample data, even in a preview tool.
        panel.update(label: name == "Two accounts" ? "Arbete" : "Claude Code",
                     view: quotaView, others: others, now: now, timeZone: tz)
        let height = panel.intrinsicContentSize.height
        panel.frame = NSRect(x: 0, y: 0, width: PanelView.width, height: height)
        guard let rep = panel.bitmapImageRepForCachingDisplay(in: panel.bounds) else { continue }
        panel.cacheDisplay(in: panel.bounds, to: rep)
        let image = NSImage(size: panel.bounds.size)
        image.addRepresentation(rep)
        images.append((name, image))
        print(String(format: "  %-16s %.0f pt tall", (name as NSString).utf8String!, height))
    }

    // Lay them out side by side on one sheet.
    let gap: CGFloat = 14
    let labelHeight: CGFloat = 18
    let maxHeight = images.map(\.1.size.height).max() ?? 400
    let sheetWidth = (PanelView.width + gap) * CGFloat(images.count) + gap
    let sheetHeight = maxHeight + labelHeight + gap * 2

    let sheet = NSImage(size: NSSize(width: sheetWidth, height: sheetHeight))
    sheet.lockFocus()
    NSColor(srgbRed: 0.12, green: 0.12, blue: 0.13, alpha: 1).setFill()
    NSRect(x: 0, y: 0, width: sheetWidth, height: sheetHeight).fill()
    var x = gap
    for (name, image) in images {
        image.draw(at: NSPoint(x: x, y: sheetHeight - gap - image.size.height),
                   from: .zero, operation: .sourceOver, fraction: 1)
        NSAttributedString(string: name, attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 11, weight: .medium),
            .foregroundColor: NSColor.white,
        ]).draw(at: NSPoint(x: x, y: gap))
        x += PanelView.width + gap
    }
    sheet.unlockFocus()

    if let tiff = sheet.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
       let png = rep.representation(using: .png, properties: [:]) {
        try? png.write(to: URL(fileURLWithPath: outputPath))
        print("\nwrote \(outputPath)")
    }
    exit(0)
}

DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { MainActor.assumeIsolated { render() } }
app.run()

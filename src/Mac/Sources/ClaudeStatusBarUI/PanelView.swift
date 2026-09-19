import AppKit
import ClaudeQuotaCore

/// The panel-v2 layout (docs/panel-v2.md), drawn with AppKit.
///
/// "The only question the panel exists to answer is: **will I break the budget before it resets,
/// and when does it reset?** It must answer that at the top, in words, every time." So the status
/// box comes first and is always shown; the big ring cards and the timelines from v1 are gone
/// (their % was duplicated by the bars and their forecast wedge read as a rendering glitch).
///
/// All text comes from `PanelText`/`TimeText` — this file decides pixels, never wording.
public final class PanelView: NSView {
    /// The width the panel asks for. It is a *request*, not a guarantee: `NSPopover` sizes its
    /// content view itself, so every measurement below reads `contentWidth` (the real bounds)
    /// rather than this constant. Drawing against the constant laid the content out for 340 pt
    /// inside whatever width AppKit actually handed over, which showed up as a left margin and a
    /// right margin that did not match.
    public static let width: CGFloat = 340

    /// The width to lay out against: whatever this view actually IS, falling back to the
    /// requested width before the first layout pass.
    private var contentWidth: CGFloat { layoutWidth ?? (bounds.width > 1 ? bounds.width : Self.width) }

    /// Set only while measuring. `intrinsicContentSize` is called by Auto Layout *while* it is
    /// deciding this view's bounds, so measuring against `bounds` is one pass stale and can
    /// oscillate. Measurement pins the width explicitly instead.
    private var layoutWidth: CGFloat?

    private var text: PanelTextResult?
    private var view: QuotaView = .initial
    private var otherAccounts: [OtherAccountRow] = []
    private var timeZone: TimeZone = .current
    private var accountLabel: String = ""
    /// The instant this panel is rendering AS OF. Everything time-dependent reads this, never
    /// `Date()` — otherwise the view cannot be rendered for a fixed moment, which is exactly what
    /// the preview and any snapshot test need, and a preview silently reports ages measured
    /// against the real wall clock instead of its own scenario.
    private var renderedAt: Date = Date()

    /// Invoked when a row in the "other accounts" list is clicked.
    public var onSelectAccount: ((Int) -> Void)?
    public var onRefresh: (() -> Void)?

    // Layout constants, all in points.
    private let margin: CGFloat = 16
    private let statusBoxPadding: CGFloat = 12
    private let barHeight: CGFloat = 10
    private let barLabelGap: CGFloat = 4
    private let sectionGap: CGFloat = 18
    private let rowHeight: CGFloat = 26

    public override var isFlipped: Bool { true }

    public func update(label: String, view: QuotaView, others: [OtherAccountRow],
                       now: Date = Date(), timeZone: TimeZone = .current) {
        self.accountLabel = label
        self.view = view
        self.otherAccounts = others
        self.timeZone = timeZone
        self.renderedAt = now
        self.text = PanelText.compose(view, now: now, timeZone: timeZone)
        invalidateIntrinsicContentSize()
        needsDisplay = true
    }

    public override var intrinsicContentSize: NSSize {
        NSSize(width: Self.width, height: measuredHeight(for: Self.width))
    }

    // MARK: - Drawing

    public override func draw(_ dirtyRect: NSRect) {
        guard let text else { return }
        NSColor.clear.setFill()
        dirtyRect.fill()

        var y = margin
        y = drawHeader(at: y)
        y = drawStatusBox(text.statusBox, at: y)
        y += sectionGap
        y = drawSection(text.session, window: view.session, at: y)
        y += sectionGap
        y = drawSection(text.weekly, window: view.weekly, at: y)
        if !otherAccounts.isEmpty {
            y += sectionGap
            y = drawOtherAccounts(at: y)
        }
        y += sectionGap
        _ = drawFooter(at: y)
    }

    /// Height the content needs at `width`. Pure: it sets no state beyond the measuring pin.
    func measuredHeight(for width: CGFloat) -> CGFloat {
        layoutWidth = width
        defer { layoutWidth = nil }
        guard let text else { return 200 }
        var y = margin
        y = drawHeader(at: y, measureOnly: true)
        y = drawStatusBox(text.statusBox, at: y, measureOnly: true)
        y += sectionGap
        y = drawSection(text.session, window: view.session, at: y, measureOnly: true)
        y += sectionGap
        y = drawSection(text.weekly, window: view.weekly, at: y, measureOnly: true)
        if !otherAccounts.isEmpty {
            y += sectionGap
            y = drawOtherAccounts(at: y, measureOnly: true)
        }
        y += sectionGap
        y = drawFooter(at: y, measureOnly: true)
        return y + margin
    }

    // MARK: - Sections

    @discardableResult
    private func drawHeader(at top: CGFloat, measureOnly: Bool = false) -> CGFloat {
        var y = top
        y = draw(accountLabel.isEmpty ? "Claude Code" : accountLabel,
                 font: .systemFont(ofSize: 13, weight: .semibold),
                 colour: .labelColor, at: y, measureOnly: measureOnly)
        y += 2
        y = draw(freshnessLine(), font: .systemFont(ofSize: 11),
                 colour: view.freshness == .live ? .secondaryLabelColor : Palette.tight.nsColor,
                 at: y, measureOnly: measureOnly)
        return y + 10
    }

    private func freshnessLine() -> String {
        switch view.freshness {
        case .unknown: return "Ingen avläsning ännu"
        case .stale, .live:
            guard let last = view.lastPollAt else { return "Väntar på första avläsningen…" }
            let age = TimeText.duration(renderedAt.timeIntervalSince(last))
            return view.freshness == .stale
                ? "Uppdaterad för \(age) sedan — kan vara inaktuell"
                : "Uppdaterad för \(age) sedan"
        }
    }

    @discardableResult
    private func drawStatusBox(_ box: StatusBoxText, at top: CGFloat, measureOnly: Bool = false) -> CGFloat {
        let colour = colour(for: box.role)
        let innerWidth = contentWidth - margin * 2 - statusBoxPadding * 2 - 4

        // Measure first so the tinted background can be drawn behind the text.
        var contentHeight = statusBoxPadding
        contentHeight += height(box.line1, font: .systemFont(ofSize: 14, weight: .semibold), width: innerWidth) + 3
        contentHeight += height(box.line2, font: .systemFont(ofSize: 11.5), width: innerWidth)
        if let line3 = box.line3 { contentHeight += 3 + height(line3, font: .systemFont(ofSize: 11), width: innerWidth) }
        if let secondary = box.secondaryLine { contentHeight += 5 + height(secondary, font: .systemFont(ofSize: 11), width: innerWidth) }
        contentHeight += statusBoxPadding

        if !measureOnly {
            let boxRect = NSRect(x: margin, y: top, width: contentWidth - margin * 2, height: contentHeight)
            let path = NSBezierPath(roundedRect: boxRect, xRadius: 7, yRadius: 7)
            colour.nsColor.withAlphaComponent(0.13).setFill()
            path.fill()
            // A state-coloured left edge, so the verdict is readable even in a screenshot with
            // the text too small to read.
            let edge = NSBezierPath(roundedRect: NSRect(x: margin, y: top, width: 4, height: contentHeight),
                                    xRadius: 2, yRadius: 2)
            colour.nsColor.setFill()
            edge.fill()
        }

        var y = top + statusBoxPadding
        let x = margin + statusBoxPadding + 4
        y = draw(box.line1, font: .systemFont(ofSize: 14, weight: .semibold), colour: colour.nsColor,
                 at: y, x: x, width: innerWidth, measureOnly: measureOnly) + 3
        y = draw(box.line2, font: .systemFont(ofSize: 11.5), colour: .labelColor,
                 at: y, x: x, width: innerWidth, measureOnly: measureOnly)
        if let line3 = box.line3 {
            y += 3
            y = draw(line3, font: .systemFont(ofSize: 11), colour: Palette.tight.nsColor,
                     at: y, x: x, width: innerWidth, measureOnly: measureOnly)
        }
        if let secondary = box.secondaryLine {
            y += 5
            y = draw(secondary, font: .systemFont(ofSize: 11), colour: .secondaryLabelColor,
                     at: y, x: x, width: innerWidth, measureOnly: measureOnly)
        }
        return top + contentHeight
    }

    @discardableResult
    private func drawSection(_ section: WindowSectionText, window: WindowView,
                             at top: CGFloat, measureOnly: Bool = false) -> CGFloat {
        var y = top
        let innerWidth = contentWidth - margin * 2

        y = draw(section.title, font: .systemFont(ofSize: 10, weight: .semibold),
                 colour: .tertiaryLabelColor, at: y, measureOnly: measureOnly)
        y = draw(section.resetHeader, font: .systemFont(ofSize: 11), colour: .secondaryLabelColor,
                 at: y + 1, width: innerWidth, measureOnly: measureOnly, alignment: .right)
        y += 8

        // Both bars share one 0–100 % scale and are left-aligned, so "quota bar shorter than
        // time bar = fine" can be read directly.
        let awaitingReset = window.measuringReason == "Nytt fönster väntas"
        let elapsed = awaitingReset ? 1.0 : elapsedFraction(window)
        y = drawBar(label: "Tid", fraction: elapsed, fill: NSColor.tertiaryLabelColor,
                    caption: section.tidLabel, at: y, measureOnly: measureOnly)
        y += 6
        y = drawKvotBar(window: window, awaitingReset: awaitingReset,
                        caption: section.kvotLabel, at: y, measureOnly: measureOnly)
        return y
    }

    private func elapsedFraction(_ window: WindowView) -> Double {
        guard let resets = window.resetsAt else { return 0 }
        let raw = (window.windowMinutes - resets.timeIntervalSince(renderedAt) / 60.0) / window.windowMinutes
        return min(max(raw, 0), 1)
    }

    @discardableResult
    private func drawBar(label: String, fraction: Double, fill: NSColor, caption: String,
                         at top: CGFloat, measureOnly: Bool) -> CGFloat {
        let labelWidth: CGFloat = 32
        let barX = margin + labelWidth
        let barWidth = contentWidth - margin * 2 - labelWidth

        if !measureOnly {
            draw(label, font: .systemFont(ofSize: 11), colour: .secondaryLabelColor,
                 at: top - 1, x: margin, width: labelWidth, measureOnly: false)
            let track = NSBezierPath(roundedRect: NSRect(x: barX, y: top, width: barWidth, height: barHeight),
                                     xRadius: barHeight / 2, yRadius: barHeight / 2)
            NSColor.quaternaryLabelColor.withAlphaComponent(0.35).setFill()
            track.fill()

            let filled = barWidth * CGFloat(min(max(fraction, 0), 1))
            if filled > 0.5 {
                let path = NSBezierPath(roundedRect: NSRect(x: barX, y: top, width: filled, height: barHeight),
                                        xRadius: barHeight / 2, yRadius: barHeight / 2)
                fill.setFill()
                path.fill()
            }
        }

        var y = top + barHeight + barLabelGap
        y = draw(caption, font: .systemFont(ofSize: 10.5), colour: .secondaryLabelColor,
                 at: y, x: barX, width: barWidth, measureOnly: measureOnly)
        return y
    }

    @discardableResult
    private func drawKvotBar(window: WindowView, awaitingReset: Bool, caption: String,
                             at top: CGFloat, measureOnly: Bool) -> CGFloat {
        let labelWidth: CGFloat = 32
        let barX = margin + labelWidth
        let barWidth = contentWidth - margin * 2 - labelWidth
        let colour = colour(for: role(for: window.state))

        if !measureOnly {
            draw("Kvot", font: .systemFont(ofSize: 11), colour: .secondaryLabelColor,
                 at: top - 1, x: margin, width: labelWidth, measureOnly: false)
            let track = NSBezierPath(roundedRect: NSRect(x: barX, y: top, width: barWidth, height: barHeight),
                                     xRadius: barHeight / 2, yRadius: barHeight / 2)
            NSColor.quaternaryLabelColor.withAlphaComponent(0.35).setFill()
            track.fill()

            // A window awaiting its next observation draws EMPTY, not the old window's usage.
            let used = awaitingReset ? 0 : (window.usedPct ?? 0) / 100
            if let projected = window.projectedPctAtReset, !awaitingReset, projected > (window.usedPct ?? 0) {
                let overflows = projected > 100
                let end = min(projected, 100) / 100
                let segment = NSRect(x: barX + barWidth * CGFloat(used), y: top,
                                     width: barWidth * CGFloat(end - used), height: barHeight)
                (overflows ? Palette.crit : colour).nsColor.withAlphaComponent(0.4).setFill()
                NSBezierPath(rect: segment).fill()
                if overflows {
                    // A marker at the end of the bar: the forecast runs past 100 %.
                    Palette.crit.nsColor.setFill()
                    NSBezierPath(rect: NSRect(x: barX + barWidth - 3, y: top - 2,
                                              width: 3, height: barHeight + 4)).fill()
                }
            }
            if used > 0.005 {
                let path = NSBezierPath(roundedRect: NSRect(x: barX, y: top, width: barWidth * CGFloat(used),
                                                            height: barHeight),
                                        xRadius: barHeight / 2, yRadius: barHeight / 2)
                colour.nsColor.setFill()
                path.fill()
            }
        }

        var y = top + barHeight + barLabelGap
        y = draw(caption, font: .systemFont(ofSize: 10.5), colour: .secondaryLabelColor,
                 at: y, x: barX, width: barWidth, measureOnly: measureOnly)
        return y
    }

    @discardableResult
    private func drawOtherAccounts(at top: CGFloat, measureOnly: Bool = false) -> CGFloat {
        var y = top
        y = draw("ANDRA KONTON", font: .systemFont(ofSize: 10, weight: .semibold),
                 colour: .tertiaryLabelColor, at: y, measureOnly: measureOnly)
        y += 4
        for row in otherAccounts {
            if !measureOnly {
                let dot = NSRect(x: margin + 1, y: y + 6, width: 7, height: 7)
                colour(for: row.role).nsColor.setFill()
                NSBezierPath(ovalIn: dot).fill()
                draw(row.label, font: .systemFont(ofSize: 11.5), colour: .labelColor,
                     at: y, x: margin + 15, width: 150, measureOnly: false)
                draw(row.line, font: .systemFont(ofSize: 11), colour: .secondaryLabelColor,
                     at: y, x: margin + 165, width: contentWidth - margin * 2 - 165,
                     measureOnly: false, alignment: .right)
            }
            y += rowHeight
        }
        return y
    }

    @discardableResult
    private func drawFooter(at top: CGFloat, measureOnly: Bool = false) -> CGFloat {
        let lastRead = view.lastPollAt.map { "Senast avläst \(TimeText.clockWithSeconds($0, timeZone: timeZone))" }
            ?? "Ingen avläsning ännu"
        let cadence = "uppdateras var \(TimeText.duration(view.pollInterval))"
        return draw("\(lastRead) · \(cadence)", font: .systemFont(ofSize: 10),
                    colour: .tertiaryLabelColor, at: top, measureOnly: measureOnly)
    }

    // MARK: - Text helpers

    @discardableResult
    private func draw(_ string: String, font: NSFont, colour: NSColor, at y: CGFloat,
                      x: CGFloat? = nil, width: CGFloat? = nil, measureOnly: Bool = false,
                      alignment: NSTextAlignment = .left) -> CGFloat {
        let originX = x ?? margin
        let boxWidth = width ?? (contentWidth - margin * 2)
        let attributes = Self.attributes(font: font, colour: colour, alignment: alignment)
        let rect = NSRect(x: originX, y: y, width: boxWidth, height: .greatestFiniteMagnitude)
        let attributed = NSAttributedString(string: string, attributes: attributes)
        let needed = attributed.boundingRect(with: NSSize(width: boxWidth, height: .greatestFiniteMagnitude),
                                             options: [.usesLineFragmentOrigin, .usesFontLeading]).height
        if !measureOnly {
            attributed.draw(with: NSRect(x: rect.minX, y: rect.minY, width: boxWidth, height: ceil(needed) + 2),
                            options: [.usesLineFragmentOrigin, .usesFontLeading])
        }
        return y + ceil(needed)
    }

    private func height(_ string: String, font: NSFont, width: CGFloat) -> CGFloat {
        let attributed = NSAttributedString(string: string,
                                            attributes: Self.attributes(font: font, colour: .labelColor,
                                                                        alignment: .left))
        return ceil(attributed.boundingRect(with: NSSize(width: width, height: .greatestFiniteMagnitude),
                                            options: [.usesLineFragmentOrigin, .usesFontLeading]).height)
    }

    private static func attributes(font: NSFont, colour: NSColor, alignment: NSTextAlignment)
        -> [NSAttributedString.Key: Any] {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = alignment
        // "All labels stay inside the panel bounds. Never clip." — wrap rather than truncate.
        paragraph.lineBreakMode = .byWordWrapping
        return [.font: font, .foregroundColor: colour, .paragraphStyle: paragraph]
    }

    private func role(for state: QuotaState) -> PanelColorRole {
        switch state {
        case .safe: return .safe
        case .tight: return .tight
        case .dryEarly: return .crit
        case .spent: return .dead
        case .measuring: return .measuring
        }
    }

    private func colour(for role: PanelColorRole) -> RGB {
        switch role {
        case .safe: return Palette.ok
        case .tight: return Palette.tight
        case .crit: return Palette.crit
        case .dead: return Palette.dead
        case .measuring, .unknown: return Palette.measuring
        }
    }

    // MARK: - Interaction

    public override func mouseDown(with event: NSEvent) {
        guard !otherAccounts.isEmpty else { return }
        let point = convert(event.locationInWindow, from: nil)
        // The rows are the last block before the footer; find which one was hit.
        var y = measuredHeight(for: contentWidth) - margin
        y -= height("x", font: .systemFont(ofSize: 10), width: 100)   // footer
        y -= sectionGap
        let rowsBottom = y
        let rowsTop = rowsBottom - CGFloat(otherAccounts.count) * rowHeight
        guard point.y >= rowsTop, point.y < rowsBottom else { return }
        let index = Int((point.y - rowsTop) / rowHeight)
        guard index >= 0, index < otherAccounts.count else { return }
        onSelectAccount?(otherAccounts[index].accountIndex)
    }
}

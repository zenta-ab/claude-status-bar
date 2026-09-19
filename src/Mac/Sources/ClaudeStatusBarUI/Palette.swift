import AppKit
import ClaudeQuotaCore

/// The agreed colour ramp (docs/forecast-and-states.md, "Colours"), identical to
/// `src/Windows/Icons/Palette.cs`. `spent` must stay `#9AA3AE` and `measuring` must stay distinct
/// from it; an earlier Windows round used `#6C7480`, which measured 1.71:1 on a dark taskbar and
/// was invisible.
public enum Palette {
    public static let ok = RGB(0x12A277)        // Safe
    public static let tight = RGB(0xE8A020)
    public static let crit = RGB(0xE23D28)      // DryEarly, and the Spent ring
    public static let dead = RGB(0x9AA3AE)      // Spent
    public static let measuring = RGB(0x5C6672)

    public static func color(for state: QuotaState) -> RGB {
        switch state {
        case .safe: return ok
        case .tight: return tight
        case .dryEarly: return crit
        // docs/forecast-and-states.md, "Exhausted": a FULL continuous ring in the real critical
        // colour, dimmed -- not the earlier dashed grey, which read as a lifebuoy, and not a
        // lightened tint, which made Spent the brightest glyph of all.
        case .spent: return crit
        case .measuring: return measuring
        }
    }

    /// The exhausted state's only brightness control. Measured on Windows against a dark taskbar;
    /// on macOS the backdrop is not fixed, so this is checked against the real one by
    /// `IconContrastTests` instead of against a single assumed colour.
    public static let spentDimAlpha: Double = 0.85
    /// Stale drops the glyph's opacity and adds a dot (docs/forecast-and-states.md, "Freshness").
    public static let staleAlpha: Double = 0.70
}

/// A plain sRGB triple. Kept separate from `NSColor` so the geometry and the contrast maths can be
/// tested without a window server, and so a colour can never silently arrive in another colour
/// space on its way to the screen.
public struct RGB: Equatable, Sendable {
    public let r, g, b: Double

    public init(_ hex: UInt32) {
        r = Double((hex >> 16) & 0xFF) / 255
        g = Double((hex >> 8) & 0xFF) / 255
        b = Double(hex & 0xFF) / 255
    }

    public init(r: Double, g: Double, b: Double) { self.r = r; self.g = g; self.b = b }

    public var cgColor: CGColor { CGColor(srgbRed: r, green: g, blue: b, alpha: 1) }
    public func cgColor(alpha: Double) -> CGColor { CGColor(srgbRed: r, green: g, blue: b, alpha: alpha) }
    public var nsColor: NSColor { NSColor(srgbRed: r, green: g, blue: b, alpha: 1) }

    /// WCAG relative luminance.
    public var relativeLuminance: Double {
        func linear(_ v: Double) -> Double { v <= 0.03928 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
    }

    /// WCAG contrast ratio, 1...21.
    public func contrast(against other: RGB) -> Double {
        let a = relativeLuminance, b = other.relativeLuminance
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }

    /// This colour composited over `backdrop` at `alpha`.
    public func composited(over backdrop: RGB, alpha: Double) -> RGB {
        RGB(r: r * alpha + backdrop.r * (1 - alpha),
            g: g * alpha + backdrop.g * (1 - alpha),
            b: b * alpha + backdrop.b * (1 - alpha))
    }
}

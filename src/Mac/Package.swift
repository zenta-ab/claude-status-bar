// swift-tools-version: 6.0
import PackageDescription

// The macOS port (docs/mac-port.md). ClaudeQuotaCore is the portable half -- the protocol
// client and, from M2 on, the forecast model ported from src/Windows/Model. quotaprobe is
// M0: a command-line target that proves Phase 1's findings in code.
let package = Package(
    name: "ClaudeStatusBarMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "ClaudeQuotaCore", targets: ["ClaudeQuotaCore"]),
        .library(name: "ClaudeStatusBarUI", targets: ["ClaudeStatusBarUI"]),
        .executable(name: "quotaprobe", targets: ["quotaprobe"]),
        .executable(name: "contactsheet", targets: ["contactsheet"]),
        .executable(name: "statusbar", targets: ["statusbar"]),
        .executable(name: "panelpreview", targets: ["panelpreview"]),
    ],
    targets: [
        .target(name: "ClaudeQuotaCore"),
        // AppKit lives here and nowhere else, so the model and protocol stay testable headless.
        .target(name: "ClaudeStatusBarUI", dependencies: ["ClaudeQuotaCore"]),
        .executableTarget(name: "quotaprobe", dependencies: ["ClaudeQuotaCore"]),
        .executableTarget(name: "contactsheet", dependencies: ["ClaudeStatusBarUI", "ClaudeQuotaCore"]),
        .executableTarget(name: "statusbar", dependencies: ["ClaudeStatusBarUI", "ClaudeQuotaCore"]),
        .executableTarget(name: "panelpreview", dependencies: ["ClaudeStatusBarUI", "ClaudeQuotaCore"]),
        .testTarget(name: "ClaudeQuotaCoreTests", dependencies: ["ClaudeQuotaCore"]),
        .testTarget(name: "ClaudeStatusBarUITests", dependencies: ["ClaudeStatusBarUI", "ClaudeQuotaCore"]),
    ]
)

import Foundation

/// The child command line `ControlChannel` launches, made injectable so tests can point the
/// channel at a fake child instead of the real `claude`. Mirrors
/// `src/Windows/Data/ChildProcessSpec.cs`; the differences are all measured facts from
/// docs/mac-port.md, "Phase 1 — evidence measured on macOS".
public struct ChildProcessSpec: Sendable {
    /// Re-resolved on EVERY launch attempt, never cached (Codex review High #1 on the Windows
    /// side). On macOS the reason is sharper than on Windows: `~/.local/bin/claude` is a
    /// *symlink* into `~/.local/share/claude/versions/<version>`, and auto-update repoints it.
    /// A path resolved once at app start goes stale the first time Claude Code updates itself.
    public var resolveExecutablePath: @Sendable () -> String
    public var arguments: [String]
    public var workingDirectory: String
    /// Environment entries forced for this child.
    public var environmentOverrides: [String: String]
    /// Environment entries removed from the inherited environment before launching. See
    /// `forAccount` for why this is not merely cosmetic.
    public var environmentRemovals: Set<String>
    /// Where this child records its pid + start time, so a later run can clean it up if it is
    /// left behind (containment mechanism 3 in `ChildProcess`).
    public var pidFileURL: URL?

    public init(
        resolveExecutablePath: @escaping @Sendable () -> String,
        arguments: [String],
        workingDirectory: String,
        environmentOverrides: [String: String] = [:],
        environmentRemovals: Set<String> = [],
        pidFileURL: URL? = nil
    ) {
        self.resolveExecutablePath = resolveExecutablePath
        self.arguments = arguments
        self.workingDirectory = workingDirectory
        self.environmentOverrides = environmentOverrides
        self.environmentRemovals = environmentRemovals
        self.pidFileURL = pidFileURL
    }

    public static let protocolArguments = [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
    ]

    /// Claude Code reads this *in preference to* `CLAUDE_CONFIG_DIR` when deciding which keychain
    /// namespace a login belongs to — see docs/mac-port.md §2 for the decompiled selector. If the
    /// user happens to have it exported, every account child would resolve the same namespace and
    /// the accounts would collide, despite each having its own `CLAUDE_CONFIG_DIR`.
    public static let secureStorageVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR"
    public static let configDirVariable = "CLAUDE_CONFIG_DIR"

    public static func `default`() -> ChildProcessSpec {
        forAccount(slot: "default", configDirectory: nil)
    }

    /// Builds the spec for one account slot (docs/multi-account.md). `slot` is an opaque
    /// per-account identifier the caller controls; it decides the working directory and the pid
    /// record, so two accounts never share one `claude` session directory.
    ///
    /// Spawning this child creates a real Claude Code session and fires SessionStart hooks, so
    /// the working directory is a dedicated agent folder, never a user repo.
    public static func forAccount(slot: String, configDirectory: String?) -> ChildProcessSpec {
        let agentRoot = agentRootURL()
        let workingDirectory = slot == "default"
            ? agentRoot.path
            : agentRoot.appendingPathComponent(slot).path

        var overrides: [String: String] = [:]
        var removals: Set<String> = []
        if let configDirectory, !configDirectory.isEmpty {
            // Phase 1 §2: Claude Code derives the login's Keychain service name from
            // `sha256(NFC(configDir))`, so the *spelling* of this path is part of the account's
            // identity. A login performed under one spelling is invisible to a child started
            // under another, which presents as an account that is silently logged out.
            let canonical = canonicalConfigDirectory(configDirectory)
            overrides[configDirVariable] = canonical
            // Pin the secure-storage selector to the same directory rather than leaving whatever
            // the user's environment holds. Setting it to the same canonical path is a no-op for
            // the resulting namespace (both spell `-sha256(canonical)[0..8]`), so this only ever
            // removes an inherited surprise.
            overrides[secureStorageVariable] = canonical
        } else {
            // The DEFAULT account deliberately inherits the user's environment unmodified: if
            // they have set CLAUDE_CONFIG_DIR for their whole session, that keeps working, and
            // whatever secure-storage directory they chose goes with it.
            removals = []
        }

        return ChildProcessSpec(
            resolveExecutablePath: { resolveClaudeExecutable() },
            arguments: protocolArguments,
            workingDirectory: workingDirectory,
            environmentOverrides: overrides,
            environmentRemovals: removals,
            pidFileURL: agentRoot.appendingPathComponent("\(slot).pid"))
    }

    public static func applicationSupportDirectory() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("ClaudeStatusBar")
    }

    public static func agentRootURL() -> URL {
        applicationSupportDirectory().appendingPathComponent("agent")
    }

    /// The one spelling of a config directory this app ever uses: absolute, symlinks resolved,
    /// no trailing slash, Unicode-normalised the same way Claude Code normalises it (NFC).
    /// See docs/mac-port.md §2, "the config-dir path must be canonical and stable".
    ///
    /// `resolvingSymlinksInPath` only resolves components that exist, so a not-yet-created
    /// directory still normalises deterministically -- the caller creates it before logging in.
    public static func canonicalConfigDirectory(_ path: String) -> String {
        let expanded = (path as NSString).expandingTildeInPath
        let url = URL(fileURLWithPath: expanded).standardizedFileURL.resolvingSymlinksInPath()
        var result = url.path
        while result.count > 1 && result.hasSuffix("/") { result.removeLast() }
        return result.precomposedStringWithCanonicalMapping   // NFC
    }

    /// The native installer's location first, then the two package managers, then `PATH`.
    ///
    /// Phase 1 §1: on this machine only `~/.local/bin/claude` exists, as a symlink to a real
    /// Mach-O binary. Unlike Windows there is no shim to screen out. `PATH` is only a backstop:
    /// a GUI app launched from Finder or as a login item does not inherit the shell's `PATH`, so
    /// in production the explicit paths are the real mechanism. When nothing is found the native
    /// path is returned, so the launch failure names where Claude Code was expected.
    public static func resolveClaudeExecutable(
        home: String = NSHomeDirectory(),
        pathVariable: String? = ProcessInfo.processInfo.environment["PATH"],
        isExecutable: (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) }
    ) -> String {
        let native = (home as NSString).appendingPathComponent(".local/bin/claude")
        let candidates = [native, "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]
        for candidate in candidates where isExecutable(candidate) { return candidate }

        for entry in (pathVariable ?? "").split(separator: ":", omittingEmptySubsequences: true) {
            let dir = entry.trimmingCharacters(in: .whitespaces)
            if dir.isEmpty { continue }
            let candidate = (dir as NSString).appendingPathComponent("claude")
            if isExecutable(candidate) { return candidate }
        }
        return native
    }
}

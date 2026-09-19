import Foundation

/// `~/Library/Application Support/ClaudeStatusBar/accounts.json`, the macOS equivalent of the
/// Windows `%LOCALAPPDATA%` file. Mirrors `src/Windows/Config/AccountsConfig.cs` and
/// docs/multi-account.md, "Configuration".
///
/// **Default install = exactly one account, `configDir: null`** — whatever the user is already
/// logged into. Zero setup, which is what a first-time user of a public tool should get.
public struct AccountsConfig: Codable, Sendable, Equatable {

    public struct Account: Codable, Sendable, Equatable {
        /// `null` means Claude Code's own default login (`~/.claude.json`).
        public var configDir: String?
        /// The user's own label override. `null` means "work it out from my Anthropic data".
        public var label: String?
        public var enabled: Bool

        public init(configDir: String? = nil, label: String? = nil, enabled: Bool = true) {
            self.configDir = configDir
            self.label = label
            self.enabled = enabled
        }
    }

    public enum DisplayMode: String, Codable, Sendable {
        /// One status item per enabled account, in config order, capped at `maxIcons`.
        case perAccount
        /// One status item, showing the account closest to being blocked.
        case binding
    }

    public var displayMode: DisplayMode
    public var maxIcons: Int
    public var accounts: [Account]

    public init(displayMode: DisplayMode = .perAccount, maxIcons: Int = 3,
                accounts: [Account] = [Account()]) {
        self.displayMode = displayMode
        self.maxIcons = maxIcons
        self.accounts = accounts
    }

    public static var fileURL: URL {
        ChildProcessSpec.applicationSupportDirectory().appendingPathComponent("accounts.json")
    }

    /// Reads the file, creating the default single-account config the first time.
    public static func load() -> AccountsConfig {
        let url = fileURL
        guard let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode(AccountsConfig.self, from: data) else {
            let fresh = AccountsConfig()
            fresh.save()
            return fresh
        }
        return decoded.normalised()
    }

    public func save() {
        let url = Self.fileURL
        try? FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(self) else { return }
        try? data.write(to: url, options: .atomic)
    }

    /// Config directories are canonicalised on the way in, because Claude Code derives the
    /// login's keychain namespace from `sha256(NFC(path))` — a trailing slash is a different
    /// account as far as it is concerned (docs/mac-port.md §2).
    public func normalised() -> AccountsConfig {
        var copy = self
        copy.maxIcons = max(1, maxIcons)
        copy.accounts = accounts.map { account in
            var a = account
            if let dir = a.configDir, !dir.isEmpty {
                a.configDir = ChildProcessSpec.canonicalConfigDirectory(dir)
            } else {
                a.configDir = nil
            }
            return a
        }
        if copy.accounts.isEmpty { copy.accounts = [Account()] }
        return copy
    }

    public var enabledAccounts: [Account] { accounts.filter(\.enabled) }

    /// Where a newly added account's config directory goes.
    public static func configDirectory(forSlot slot: Int) -> String {
        ChildProcessSpec.canonicalConfigDirectory(
            ChildProcessSpec.applicationSupportDirectory()
                .appendingPathComponent("accounts/\(slot)/config").path)
    }
}

import Foundation

/// Who a config directory is logged in as. Mirrors `src/Windows/Data/AccountIdentity.cs`.
///
/// **The identity is the account+organisation PAIR, never `accountUuid` alone.** One person can
/// hold two plans — a Team seat and a personal Max seat — in two config directories, and Anthropic
/// hands both logins the *same* `accountUuid`; only `organizationUuid` differs. Keying on
/// `accountUuid` alone collapsed both onto one key on Windows: one log directory, one CSV, one
/// model fed two plans' contradictory percentages, and one tray icon where there should have been
/// two (docs/multi-account.md, "Identity guard").
public struct AccountIdentity: Equatable, Sendable {
    public let accountUuid: String
    public let organizationUuid: String?
    public let organizationName: String?
    public let emailAddress: String?
    public let organizationType: String?

    public init(accountUuid: String, organizationUuid: String?, organizationName: String?,
                emailAddress: String?, organizationType: String?) {
        self.accountUuid = accountUuid
        self.organizationUuid = organizationUuid
        self.organizationName = organizationName
        self.emailAddress = emailAddress
        self.organizationType = organizationType
    }

    /// `<accountUuid>_<organizationUuid>`, or `accountUuid` alone for a login with no organisation.
    /// Everything per-account lives under this: the log directory, the model, warm start, freshness.
    public var stateKey: String {
        guard let organizationUuid, !organizationUuid.isEmpty else { return accountUuid }
        return "\(accountUuid)_\(organizationUuid)"
    }

    /// At most 8 characters of any identifier may reach a log line, and the rule applies to **each
    /// half independently** — truncating the joined string would collapse two organisations for
    /// the same person back onto one prefix.
    public var logSafePrefix: String {
        let account = String(accountUuid.prefix(8))
        guard let organizationUuid, !organizationUuid.isEmpty else { return account }
        return "\(account)_\(String(organizationUuid.prefix(8)))"
    }

    /// Reads `<configDir>/.claude.json`, or `~/.claude.json` for the default login.
    ///
    /// The asymmetry is real and measured (docs/mac-port.md §2): for the DEFAULT account Claude
    /// Code keeps `.claude.json` as a *sibling* of `~/.claude`, not inside it. Under
    /// `CLAUDE_CONFIG_DIR` the whole state directory relocates and the file does live inside.
    public static func read(configDirectory: String?) -> AccountIdentity? {
        let path: String
        if let configDirectory, !configDirectory.isEmpty {
            path = (configDirectory as NSString).appendingPathComponent(".claude.json")
        } else {
            path = (NSHomeDirectory() as NSString).appendingPathComponent(".claude.json")
        }
        guard let data = FileManager.default.contents(atPath: path),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oauth = root["oauthAccount"] as? [String: Any],
              let accountUuid = oauth["accountUuid"] as? String, !accountUuid.isEmpty else { return nil }

        return AccountIdentity(
            accountUuid: accountUuid,
            organizationUuid: oauth["organizationUuid"] as? String,
            organizationName: oauth["organizationName"] as? String,
            emailAddress: oauth["emailAddress"] as? String,
            organizationType: oauth["organizationType"] as? String)
    }
}

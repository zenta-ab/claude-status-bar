using System.Text.Json;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Identity and label material read from one config directory's .claude.json -&gt;
/// oauthAccount (docs/multi-account.md). Never throws: a missing file, a missing
/// oauthAccount key, a missing/empty accountUuid, or malformed JSON all yield a
/// null identity -- callers must treat that exactly like "not logged in / not
/// known yet" rather than a fault, since it is read on a plain background/poll
/// cadence and the file can legitimately be absent or mid-write at any moment.
///
/// IDENTITY KEY (docs/multi-account.md "Identity guard" -- a live bug on this machine):
/// AccountUuid identifies the PERSON, not the plan. One person can hold a Team seat and a
/// personal Max seat at once, each in its own config directory, and Anthropic hands both
/// logins the SAME accountUuid -- only OrganizationUuid differs between them. Keying
/// per-account state on AccountUuid alone collapses two real accounts into one: one log
/// directory, one CSV, one QuotaModel fed two plans' contradictory percentages. StateKey is
/// the (AccountUuid, OrganizationUuid) pair -- everything downstream (log directory, CSV,
/// warm start, identity-change detection) must key on StateKey, never on AccountUuid alone.
///
/// PRIVACY (this is a public repo -- see CLAUDE.md/no-personal-traces-public-repos):
/// EmailAddress and DisplayName must NEVER be logged or written to disk anywhere in
/// this app. The only account-identifying text that may ever reach a log line is
/// UuidPrefix/KeyPrefixOf (at most the first 8 characters of each identifier).
/// </summary>
public sealed record AccountIdentity(
    string AccountUuid,
    string? OrganizationUuid,
    string? OrganizationName,
    string? OrganizationType,
    string? SeatTier,
    string? BillingType,
    string? EmailAddress,
    string? DisplayName)
{
    /// <summary>
    /// The per-account identity key (see the type doc comment): AccountUuid and
    /// OrganizationUuid joined with an underscore, or AccountUuid alone when this login has no
    /// organisation (OrganizationUuid null/empty -- a bare personal login). Filesystem-safe:
    /// both source values are UUIDs (hex digits and dashes only).
    /// </summary>
    public string StateKey => StateKeyOf(AccountUuid, OrganizationUuid);

    /// <summary>Same composition as StateKey, usable without a full AccountIdentity in hand.</summary>
    public static string StateKeyOf(string accountUuid, string? organizationUuid) =>
        string.IsNullOrEmpty(organizationUuid) ? accountUuid : $"{accountUuid}_{organizationUuid}";

    /// <summary>Log-safe: at most the first 8 characters of AccountUuid, never anything else about this identity.</summary>
    public string UuidPrefix => UuidPrefixOf(AccountUuid);

    /// <summary>Same truncation as UuidPrefix, usable without a full AccountIdentity in hand (e.g. logging just an accountUuid string).</summary>
    public static string UuidPrefixOf(string? uuid) =>
        string.IsNullOrEmpty(uuid) ? "?" : uuid.Length <= 8 ? uuid : uuid[..8];

    /// <summary>
    /// Log-safe form of a StateKey: each identifier making it up (AccountUuid and, when
    /// present, OrganizationUuid) truncated to at most 8 characters the same way UuidPrefixOf
    /// does, then rejoined -- so two different organisations for the same person still show up
    /// as two distinct log-safe prefixes instead of collapsing back onto one.
    /// </summary>
    public static string KeyPrefixOf(string? stateKey)
    {
        if (string.IsNullOrEmpty(stateKey)) return "?";
        string[] parts = stateKey.Split('_', 2);
        return parts.Length == 2 ? $"{UuidPrefixOf(parts[0])}_{UuidPrefixOf(parts[1])}" : UuidPrefixOf(parts[0]);
    }

    /// <summary>
    /// Reads &lt;configDir&gt;\.claude.json -&gt; oauthAccount. Returns null on anything short of a
    /// fully readable file with a valid JSON object and a non-empty accountUuid -- a missing
    /// file, a missing oauthAccount key, a malformed document, or an IO error (e.g. the CLI is
    /// mid-write) are all treated identically as "identity not available right now".
    /// </summary>
    public static AccountIdentity? ReadFrom(string configDir)
    {
        try
        {
            string path = Path.Combine(configDir, ".claude.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("oauthAccount", out JsonElement oa) || oa.ValueKind != JsonValueKind.Object)
                return null;

            string? accountUuid = ReadString(oa, "accountUuid");
            if (string.IsNullOrEmpty(accountUuid)) return null; // everything downstream keys on this; meaningless without it

            return new AccountIdentity(
                accountUuid,
                ReadString(oa, "organizationUuid"),
                ReadString(oa, "organizationName"),
                ReadString(oa, "organizationType"),
                ReadString(oa, "seatTier"),
                ReadString(oa, "billingType"),
                ReadString(oa, "emailAddress"),
                ReadString(oa, "displayName"));
        }
        catch
        {
            return null; // malformed JSON, IO error (locked/mid-write), whatever -- never throw
        }
    }

    static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>CLAUDE_CONFIG_DIR if set, else %USERPROFILE%\.claude -- exactly how Claude Code itself resolves the default account's config directory (docs/multi-account.md).</summary>
    public static string ResolveDefaultConfigDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } explicitDir
            ? explicitDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Where AccountRuntime looks for the DEFAULT account's .claude.json. Deliberately NOT the
    /// same value as ResolveDefaultConfigDir(): verified against a real, already-logged-in
    /// installation (this task's own live-verification step), Claude Code writes the identity/
    /// oauthAccount file to %USERPROFILE%\.claude.json directly -- a SIBLING of the
    /// %USERPROFILE%\.claude directory, not inside it. %USERPROFILE%\.claude only holds
    /// credentials/plugins/CLAUDE.md/etc. A literal "&lt;ResolveDefaultConfigDir()&gt;\.claude.json"
    /// composition (%USERPROFILE%\.claude\.claude.json) does not exist on disk and silently left
    /// every default-account identity read at null, defeating the whole per-account CSV/model
    /// keying this exists for. When CLAUDE_CONFIG_DIR IS set, this app has no independent way to
    /// verify Claude Code's layout choice for that custom directory, so it keeps the spec's
    /// literal "&lt;configDir&gt;\.claude.json" composition there -- consistent with
    /// CLAUDE_CONFIG_DIR being documented as relocating the whole state directory, not just the
    /// credentials-shaped subset %USERPROFILE%\.claude holds by default.
    /// </summary>
    public static string ResolveDefaultIdentityDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } explicitDir
            ? explicitDir
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

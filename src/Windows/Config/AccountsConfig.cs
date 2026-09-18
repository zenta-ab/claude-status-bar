using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Config;

/// <summary>docs/multi-account.md "Display": perAccount shows one icon per enabled account (capped at MaxIcons); binding shows one icon for the account closest to being blocked.</summary>
public enum AccountDisplayMode
{
    PerAccount,
    Binding,
}

/// <summary>
/// One entry in accounts.json's "accounts" array. ConfigDir null means "Claude Code's own
/// default login" (docs/multi-account.md) -- resolved the same way Claude Code itself
/// resolves it, see Data/AccountIdentity.ResolveDefaultConfigDir.
/// </summary>
public sealed class AccountEntry
{
    public string? ConfigDir { get; set; }
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Unknown per-account fields round-tripped through Load/Save ("preserved where
    /// practical" -- this is the practical part: it costs nothing beyond one extension-data
    /// property, and means a future version's or a hand-added field never silently vanishes
    /// the next time this app rewrites the file).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

/// <summary>
/// %LOCALAPPDATA%\ClaudeStatusBar\accounts.json, per docs/multi-account.md. Load/Save never
/// throw: a missing file gets the one-account default created on first run (zero setup for a
/// first-time user); a malformed or partially-invalid file falls back to defaults (logged,
/// never silently) and the broken file is renamed to accounts.bad-&lt;timestamp&gt;.json rather
/// than being overwritten and lost -- the user (or a bug report) can still inspect it.
/// </summary>
public sealed class AccountsConfig
{
    public AccountDisplayMode DisplayMode { get; set; } = AccountDisplayMode.PerAccount;
    public int MaxIcons { get; set; } = 3;
    public List<AccountEntry> Accounts { get; set; } = new();

    /// <summary>Unknown top-level fields, preserved the same way AccountEntry.ExtraFields is.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "accounts.json");

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The one-account default: configDir null (Claude Code's own login), enabled.</summary>
    public static AccountsConfig Default() => new()
    {
        DisplayMode = AccountDisplayMode.PerAccount,
        MaxIcons = 3,
        Accounts = new List<AccountEntry> { new() { ConfigDir = null, Label = null, Enabled = true } },
    };

    /// <summary>
    /// Loads accounts.json, creating the one-account default file the first time it is called
    /// against a path that does not exist yet. Never throws: any failure (missing directory
    /// permissions aside, which Save itself already tolerates) degrades to an in-memory default
    /// so the app always has a usable config, exactly like a cold start.
    /// </summary>
    public static AccountsConfig LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                AccountsConfig created = Default();
                Save(created, path);
                return created;
            }

            string text = File.ReadAllText(path);
            AccountsConfig? parsed = JsonSerializer.Deserialize<AccountsConfig>(text, SerializerOptions);
            if (parsed is null || !IsValid(parsed))
                throw new InvalidDataException("accounts.json failed validation");
            return parsed;
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"accounts.json invalid ({ex.GetType().Name}: {ex.Message}); falling back to defaults");
            Quarantine(path);
            AccountsConfig fallback = Default();
            try { Save(fallback, path); }
            catch (Exception saveEx) { SafeLog.Warn($"failed writing default accounts.json: {saveEx.Message}"); }
            return fallback;
        }
    }

    /// <summary>
    /// Structurally valid JSON can still be a semantically broken config (an empty accounts
    /// array, a non-positive maxIcons) -- "partially-invalid" in the task's own words. Both are
    /// treated exactly like a parse failure: fall back to defaults rather than run with a config
    /// that can never produce a usable account list or icon cap.
    /// </summary>
    static bool IsValid(AccountsConfig config) =>
        config.Accounts is { Count: > 0 } && config.MaxIcons >= 1;

    /// <summary>Write-then-rename so a crash mid-write can never leave a half-written, corrupt accounts.json behind.</summary>
    public static void Save(AccountsConfig config, string? path = null)
    {
        path ??= DefaultPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path)!;
            string bad = Path.Combine(dir, $"accounts.bad-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Move(path, bad, overwrite: true);
            SafeLog.Warn($"quarantined malformed accounts.json as {Path.GetFileName(bad)}");
        }
        catch (Exception ex)
        {
            // Quarantine is best-effort: losing the ability to inspect the bad file later is
            // strictly better than crashing the app over it now.
            SafeLog.Warn($"failed to quarantine malformed accounts.json: {ex.Message}");
        }
    }
}

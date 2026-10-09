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
/// One entry in accounts.json's "accounts" array: the metadata for one app-owned login slot
/// (docs/multi-account.md "Slots"). The slot folder, not this entry, is the source of truth for
/// whether an account exists -- Config/AccountReconciler.cs rebuilds this list from the folders.
/// </summary>
public sealed class AccountEntry
{
    /// <summary>The slot id, i.e. the folder name under %LOCALAPPDATA%\ClaudeStatusBar\accounts.</summary>
    public string? Slot { get; set; }
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Version 1 stored a "configDir" path instead of a slot id. It is read once so the migration
    /// (AccountReconciler.Migrate) can turn it into a slot id, then cleared and never written again.
    /// </summary>
    [JsonPropertyName("configDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyConfigDir { get; set; }

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
/// docs/multi-account.md "Panels": what clicking an icon opens. Single (the default): the detailed
/// panel of one account. Full: every enabled account's full panel, side by side. Cards: one panel with a
/// compact card per enabled account. Persisted as "single" | "full" | "cards"; the earlier value "all"
/// is read as "cards" and written back as "cards" the next time the file is saved.
/// </summary>
[JsonConverter(typeof(PanelDisplayModeConverter))]
public enum PanelDisplayMode
{
    Single,
    Full,
    Cards,
}

/// <summary>Reads "single" | "full" | "cards" (and the old "all" as cards); anything else is an error, so the file is quarantined like any other invalid one.</summary>
public sealed class PanelDisplayModeConverter : JsonConverter<PanelDisplayMode>
{
    public override PanelDisplayMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return text?.ToLowerInvariant() switch
        {
            "single" => PanelDisplayMode.Single,
            "full" => PanelDisplayMode.Full,
            "cards" or "all" => PanelDisplayMode.Cards,
            _ => throw new JsonException("unknown panelMode"),
        };
    }

    public override void Write(Utf8JsonWriter writer, PanelDisplayMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PanelDisplayMode.Full => "full",
            PanelDisplayMode.Cards => "cards",
            _ => "single",
        });
}

public enum AccountsConfigStatus
{
    /// <summary>Read and valid.</summary>
    Loaded,
    /// <summary>No file yet (first run): an empty account set, to be filled from the slots.</summary>
    Missing,
    /// <summary>Unreadable or invalid: renamed to accounts.bad-&lt;timestamp&gt;.json, an empty account set stands in for it.</summary>
    Quarantined,
}

public readonly record struct AccountsConfigLoad(AccountsConfig Config, AccountsConfigStatus Status);

/// <summary>
/// %LOCALAPPDATA%\ClaudeStatusBar\accounts.json (docs/multi-account.md "Configuration"):
/// { "version": 2, "displayMode", "maxIcons", "accounts": [ { "slot", "label", "enabled" } ] }.
/// Metadata only -- the login slots are the source of truth, and an empty list is valid (the app
/// then shows its "log in" state). Load/Save never throw: a malformed or invalid file is renamed
/// to accounts.bad-&lt;timestamp&gt;.json rather than overwritten (the user, or a bug report, can
/// still inspect it) and an empty config stands in; the caller then rebuilds the account set from
/// the active slots, so no login is ever orphaned by a lost file.
/// </summary>
public sealed class AccountsConfig
{
    public const int CurrentVersion = 2;

    List<AccountEntry> _accounts = new();

    /// <summary>0 = the field was absent, i.e. a version 1 file; see AccountReconciler.Migrate.</summary>
    public int Version { get; set; }
    public AccountDisplayMode DisplayMode { get; set; } = AccountDisplayMode.PerAccount;
    public int MaxIcons { get; set; } = 3;

    /// <summary>
    /// "panelMode": "single" | "full" | "cards". Optional and absent by default (= single), so an
    /// existing accounts.json is not changed until the user picks another panel from the tray menu.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PanelDisplayMode? PanelMode { get; set; }

    /// <summary>The effective mode: single unless "panelMode" says otherwise.</summary>
    [JsonIgnore]
    public PanelDisplayMode EffectivePanelMode => PanelMode ?? PanelDisplayMode.Single;

    /// <summary>"accounts": null is the same as an empty list.</summary>
    public List<AccountEntry> Accounts
    {
        get => _accounts;
        set => _accounts = value ?? new List<AccountEntry>();
    }

    /// <summary>Unknown top-level fields, preserved the same way AccountEntry.ExtraFields is.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "accounts.json");

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new PanelDisplayModeConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>A new, empty, current-version config.</summary>
    public static AccountsConfig Empty() => new() { Version = CurrentVersion };

    /// <summary>
    /// Loads accounts.json. Never throws. A missing file is not an error (first run); an
    /// unreadable or invalid one is quarantined and replaced, in memory, by an empty config. The
    /// file itself is not (re)written here -- the caller saves once it has reconciled the account
    /// set against the slots.
    /// </summary>
    public static AccountsConfigLoad Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AccountsConfigLoad(Empty(), AccountsConfigStatus.Missing);

            string text = File.ReadAllText(path);
            AccountsConfig? parsed = JsonSerializer.Deserialize<AccountsConfig>(text, SerializerOptions);
            // A version 1 file is migrated, not judged: an out-of-range icon cap there is clamped, so the
            // accounts it lists are not quarantined away over a setting. (A version 2 file is ours.)
            if (parsed is { Version: < CurrentVersion, MaxIcons: < 1 }) parsed.MaxIcons = 1;
            if (parsed is null || !IsValid(parsed))
                throw new InvalidDataException("accounts.json failed validation");
            return new AccountsConfigLoad(parsed, AccountsConfigStatus.Loaded);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"accounts.json invalid ({ex.GetType().Name}); the account set is rebuilt from the login slots");
            Quarantine(path);
            return new AccountsConfigLoad(Empty(), AccountsConfigStatus.Quarantined);
        }
    }

    /// <summary>
    /// Structurally valid JSON can still be a semantically broken config -- a non-positive maxIcons
    /// can never produce a usable icon cap. An empty (or null) accounts list is VALID.
    /// </summary>
    static bool IsValid(AccountsConfig config) => config.MaxIcons >= 1;

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

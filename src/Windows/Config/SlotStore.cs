using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Config;

/// <summary>pending = login in progress (or interrupted), active = a tracked account, retired = being deleted, never adopted (docs/multi-account.md "Slots").</summary>
public enum SlotState { Pending, Active, Retired }

/// <summary>
/// The contents of slot.json. Deliberately carries no identity and no email: it only says what
/// this app has decided to do with the folder next to it.
/// </summary>
public sealed record SlotMarker(SlotState State, DateTimeOffset CreatedUtc, int Schema = SlotStore.MarkerSchema);

/// <summary>A folder under the accounts root with a valid slot id; Marker is null when it has no (readable) slot.json, i.e. a legacy folder this app did not create.</summary>
public sealed record SlotInfo(string Id, SlotMarker? Marker);

/// <summary>
/// The app-owned login folders (docs/multi-account.md "Slots"):
///
///   %LOCALAPPDATA%\ClaudeStatusBar\accounts\&lt;id&gt;\config      the Claude config dir (the login lives here)
///   %LOCALAPPDATA%\ClaudeStatusBar\accounts\&lt;id&gt;\slot.json   { "state", "createdUtc", "schema" }
///
/// This is the ONE place that decides whether a path may be logged into, deleted or spawned
/// against. Every login / logout / delete / spawn-with-writes goes through the PATH GUARD below
/// (TryGuardConfigDir / TryGuardSlotDir / ConfigDirOf / SlotDirOf), which accepts exactly
/// `&lt;root&gt;\&lt;id&gt;\config` (or `&lt;root&gt;\&lt;id&gt;`), with `&lt;id&gt;` matching
/// `^([0-9]+|[0-9a-f]{8})$`, and refuses anything else -- traversal, a different root, UNC, the
/// user's own %USERPROFILE%\.claude, trailing separators, alternative spellings, and any component
/// that is a reparse point (junction / symlink). The check is on the literal string, never on a
/// normalised form of it, so no clever spelling can resolve into the accepted shape.
///
/// Reparse points are checked from the accounts root downwards (root, slot dir, config dir): the
/// parts of the tree this app creates. Parents of the root belong to the user's profile and may
/// legitimately be redirected.
/// </summary>
public class SlotStore
{
    public const int MarkerSchema = 1;
    public const string MarkerFileName = "slot.json";
    public const string ConfigFolderName = "config";

    // \z, not $: `$` would also accept a trailing newline. [0-9], not \d: \d matches other scripts' digits.
    static readonly Regex IdPattern = new(@"^([0-9]+|[0-9a-f]{8})\z", RegexOptions.CultureInvariant);

    static readonly JsonSerializerOptions MarkerJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>%LOCALAPPDATA%\ClaudeStatusBar\accounts</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "accounts");

    public static SlotStore Default { get; } = new(DefaultRoot);

    /// <summary>The canonical accounts root: full path, no trailing separator.</summary>
    public string Root { get; }

    readonly Func<string> _idSource;

    /// <param name="idSource">Test-only: replaces the random id generator, so a collision with an existing folder can be forced.</param>
    public SlotStore(string accountsRoot, Func<string>? idSource = null)
    {
        Root = Path.GetFullPath(accountsRoot).TrimEnd('\\', '/');
        _idSource = idSource ?? (() => RandomNumberGenerator.GetHexString(8, lowercase: true));
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    // ---- the path guard ----

    /// <summary>True only for exactly &lt;root&gt;\&lt;id&gt;\config. `canonical` is this store's own spelling of it (what callers must pass on, never the input).</summary>
    public bool TryGuardConfigDir(string? path, [NotNullWhen(true)] out string? slotId, [NotNullWhen(true)] out string? canonical) =>
        TryGuard(path, wantConfig: true, out slotId, out canonical);

    /// <summary>True only for exactly &lt;root&gt;\&lt;id&gt; -- the form deletion needs.</summary>
    public bool TryGuardSlotDir(string? path, [NotNullWhen(true)] out string? slotId, [NotNullWhen(true)] out string? canonical) =>
        TryGuard(path, wantConfig: false, out slotId, out canonical);

    /// <summary>The guarded config dir for a slot id; throws (and logs) if the id or the folder structure is refused.</summary>
    public string ConfigDirOf(string slotId) => GuardedOrThrow(slotId, wantConfig: true);

    /// <summary>The guarded slot dir for a slot id; throws (and logs) if refused.</summary>
    public string SlotDirOf(string slotId) => GuardedOrThrow(slotId, wantConfig: false);

    string GuardedOrThrow(string slotId, bool wantConfig)
    {
        if (IsValidId(slotId))
        {
            string candidate = wantConfig ? Path.Combine(Root, slotId, ConfigFolderName) : Path.Combine(Root, slotId);
            if (TryGuard(candidate, wantConfig, out _, out string? canonical)) return canonical;
        }
        else
        {
            SafeLog.Warn($"path guard refused slot id (not a valid slot id): length {slotId?.Length ?? 0}");
        }
        throw new UnauthorizedAccessException($"the path guard refused slot '{(IsValidId(slotId) ? slotId : "<invalid>")}'");
    }

    bool TryGuard(string? path, bool wantConfig, [NotNullWhen(true)] out string? slotId, [NotNullWhen(true)] out string? canonical)
    {
        slotId = null;
        canonical = null;

        if (string.IsNullOrEmpty(path)) return Refuse("empty path");

        string prefix = Root + "\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Refuse("outside the accounts root");

        string[] parts = path[prefix.Length..].Split('\\');
        if (wantConfig)
        {
            if (parts.Length != 2 || !string.Equals(parts[1], ConfigFolderName, StringComparison.OrdinalIgnoreCase))
                return Refuse("not <root>\\<id>\\config");
        }
        else if (parts.Length != 1)
        {
            return Refuse("not <root>\\<id>");
        }

        if (!IsValidId(parts[0])) return Refuse("slot id has the wrong shape");

        string slotDir = Path.Combine(Root, parts[0]);
        string configDir = Path.Combine(slotDir, ConfigFolderName);
        if (IsReparsePoint(Root) || IsReparsePoint(slotDir) || (wantConfig && IsReparsePoint(configDir)))
            return Refuse("a path component is a reparse point");

        slotId = parts[0];
        canonical = wantConfig ? configDir : slotDir;
        return true;

        bool Refuse(string reason)
        {
            SafeLog.Warn($"path guard refused a path: {reason}");
            return false;
        }
    }

    static bool IsReparsePoint(string path)
    {
        try
        {
            // GetAttributes works for files and directories; a missing path is simply not a reparse point.
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception)
        {
            return true; // cannot tell -> fail closed
        }
    }

    // ---- canonical spelling of a path an OLDER version wrote ----

    /// <summary>
    /// Version 1 accounts.json files hold `configDir` strings written by an earlier script or app,
    /// in whatever spelling it used. Before such a string meets the (literal) guard it is put into
    /// canonical form: full path, backslash separators, `..` resolved, no trailing separator, 8.3 short
    /// names expanded to long names, and the root spelled the way this store spells it. Case is left
    /// to the guard, which ignores it. Anything that cannot be made canonical comes back unchanged
    /// (and the guard then refuses it).
    /// </summary>
    public string CanonicalizeLegacyPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string fullLong = ToLongPath(full);
            string rootLong = ToLongPath(Root);
            return fullLong.StartsWith(rootLong + "\\", StringComparison.OrdinalIgnoreCase)
                ? Root + fullLong[rootLong.Length..]
                : fullLong;
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    /// <summary>Expands 8.3 short names (GetLongPathName). A part that does not exist yet is kept as written, after expanding the longest existing parent.</summary>
    public static string ToLongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            if (length > 0 && length < buffer.Capacity) return buffer.ToString();

            string? parent = Path.GetDirectoryName(path);
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && leaf.Length > 0) return Path.Combine(ToLongPath(parent), leaf);
        }
        catch
        {
            // fall through: the guard will judge the string as written
        }
        return path;
    }

    // ---- slot lifecycle ----

    /// <summary>
    /// Creates a new pending slot and returns its id: 8 random lowercase hex characters, never a
    /// name that already exists in any form (a retired or half-deleted folder included), so an id
    /// is never reused.
    /// </summary>
    public string CreatePending()
    {
        Directory.CreateDirectory(Root);
        string id;
        do
        {
            id = _idSource();
        }
        while (Directory.Exists(Path.Combine(Root, id)) || File.Exists(Path.Combine(Root, id)));

        Directory.CreateDirectory(ConfigDirOf(id));
        WriteMarker(id, SlotState.Pending);
        return id;
    }

    /// <summary>All folders directly under the root whose name is a valid slot id, with their marker (null when absent/unreadable). Reparse-point folders are skipped: this app never follows them.</summary>
    public IReadOnlyList<SlotInfo> Enumerate()
    {
        var result = new List<SlotInfo>();
        if (!Directory.Exists(Root) || IsReparsePoint(Root)) return result;

        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            string name = Path.GetFileName(dir);
            if (!IsValidId(name) || IsReparsePoint(dir)) continue;
            result.Add(new SlotInfo(name, ReadMarker(name)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    /// <summary>slot.json for a slot, or null when missing, malformed, or of an unknown state.</summary>
    public SlotMarker? ReadMarker(string slotId)
    {
        try
        {
            string path = Path.Combine(SlotDirOf(slotId), MarkerFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SlotMarker>(File.ReadAllText(path), MarkerJson);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes slot.json (write-then-rename), keeping the original createdUtc when one is already recorded.</summary>
    public virtual void WriteMarker(string slotId, SlotState state)
    {
        string dir = SlotDirOf(slotId);
        DateTimeOffset created = ReadMarker(slotId)?.CreatedUtc ?? DateTimeOffset.UtcNow;
        string json = JsonSerializer.Serialize(new SlotMarker(state, created, MarkerSchema), MarkerJson);
        string path = Path.Combine(dir, MarkerFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes a slot folder. Order matters: the config dir (the login) goes first, the marker last,
    /// so an interrupted delete still leaves a `retired` marker for the next start to finish.
    /// Returns true when the folder is gone. Never throws.
    /// </summary>
    public virtual bool TryDelete(string slotId)
    {
        try
        {
            string slotDir = SlotDirOf(slotId);
            if (!Directory.Exists(slotDir)) return true;

            string configDir = Path.Combine(slotDir, ConfigFolderName);
            if (Directory.Exists(configDir) && !IsReparsePoint(configDir))
                Directory.Delete(configDir, recursive: true); // .NET removes a nested junction itself; it never recurses through one

            Directory.Delete(slotDir, recursive: true);
            return !Directory.Exists(slotDir);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"slot {slotId}: delete incomplete ({ex.GetType().Name}); retired marker stays for the next start");
            return false;
        }
    }

    /// <summary>TryDelete with a few spaced retries (a just-stopped child can hold files for a moment).</summary>
    public virtual async Task<bool> DeleteWithRetryAsync(string slotId, int attempts = 4, int delayMs = 500)
    {
        for (int i = 0; i < attempts; i++)
        {
            // Every attempt, the first included, runs on the thread pool: a recursive delete of a login folder
            // (thousands of small files) must never run on the UI thread that awaits this.
            if (await Task.Run(() => TryDelete(slotId)).ConfigureAwait(false)) return true;
            if (i + 1 < attempts) await Task.Delay(delayMs).ConfigureAwait(false);
        }
        return false;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// docs/statistics.md, decision 4 "Proactive advice": persists Model/StatisticsAdvice.LastShown
/// per account in a small JSON file next to accounts.json, so a restart never re-shows a
/// recommendation the user already saw for the exact same underlying pattern. Same
/// write-temp-then-rename discipline as Config/AccountsConfig.cs; unlike accounts.json this file
/// is purely a UI convenience (never user-edited), so a parse failure degrades to "nothing was
/// ever shown" rather than quarantining anything -- worst case, one recommendation re-shows once.
/// </summary>
public static class StatisticsAdviceStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "statistics-advice.json");

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    sealed class FileShape
    {
        public Dictionary<string, Entry> Accounts { get; set; } = new();
    }

    sealed class Entry
    {
        public string? CeilingSignature { get; set; }
        public string? WeeklyBudgetSignature { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    }

    /// <summary>Never throws: a missing/corrupt file yields "nothing shown for any account yet".</summary>
    public static Dictionary<string, StatisticsAdvice.LastShown> LoadAll(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new();
            FileShape? parsed = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), SerializerOptions);
            if (parsed is null) return new();
            var result = new Dictionary<string, StatisticsAdvice.LastShown>();
            foreach ((string key, Entry entry) in parsed.Accounts)
                result[key] = new StatisticsAdvice.LastShown(entry.CeilingSignature, entry.WeeklyBudgetSignature);
            return result;
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"statistics-advice.json invalid ({ex.GetType().Name}: {ex.Message}); treating as empty");
            return new();
        }
    }

    /// <summary>Reads, updates one account's entry, writes back atomically (temp file + rename). Best-effort: a write failure is logged, never thrown.</summary>
    public static void SaveOne(string accountKey, StatisticsAdvice.LastShown state, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Dictionary<string, StatisticsAdvice.LastShown> all = LoadAll(path);
            all[accountKey] = state;

            var shape = new FileShape();
            foreach ((string key, StatisticsAdvice.LastShown value) in all)
                shape.Accounts[key] = new Entry { CeilingSignature = value.CeilingSignature, WeeklyBudgetSignature = value.WeeklyBudgetSignature };

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(shape, SerializerOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"failed to persist statistics-advice.json: {ex.Message}");
        }
    }
}

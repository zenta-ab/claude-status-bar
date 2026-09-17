using ClaudeStatusBar.Config;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// %LOCALAPPDATA%\ClaudeStatusBar\accounts.json (docs/multi-account.md "Configuration"):
/// defaults on first run, malformed-file recovery (quarantined, never crashes), round-trip
/// save/load, and unknown-field preservation.
/// </summary>
public class AccountsConfigTests
{
    static string NewPath(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "accounts.json");
    }

    static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void LoadOrCreateDefault_FirstRun_CreatesOneAccountFile()
    {
        string path = NewPath("accounts-firstrun");
        try
        {
            Assert.False(File.Exists(path));

            AccountsConfig config = AccountsConfig.LoadOrCreateDefault(path);

            Assert.True(File.Exists(path));
            Assert.Equal(AccountDisplayMode.PerAccount, config.DisplayMode);
            Assert.Equal(3, config.MaxIcons);
            Assert.Single(config.Accounts);
            Assert.Null(config.Accounts[0].ConfigDir);
            Assert.Null(config.Accounts[0].Label);
            Assert.True(config.Accounts[0].Enabled);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void LoadOrCreateDefault_MalformedJson_FallsBackToDefaults_AndKeepsTheBadFile()
    {
        string path = NewPath("accounts-malformed");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");

            AccountsConfig config = AccountsConfig.LoadOrCreateDefault(path);

            Assert.Single(config.Accounts); // fell back to the default, one-account config
            Assert.Null(config.Accounts[0].ConfigDir);

            // The broken file was renamed, not overwritten or deleted -- still inspectable.
            string? dir = Path.GetDirectoryName(path);
            string[] badFiles = Directory.GetFiles(dir!, "accounts.bad-*.json");
            Assert.Single(badFiles);
            Assert.Contains("this is not valid json", File.ReadAllText(badFiles[0]));

            // A fresh, valid default file now lives at the original path.
            Assert.True(File.Exists(path));
            AccountsConfig reloaded = AccountsConfig.LoadOrCreateDefault(path);
            Assert.Single(reloaded.Accounts);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void LoadOrCreateDefault_PartiallyInvalid_EmptyAccountsArray_FallsBackToDefaults()
    {
        string path = NewPath("accounts-empty");
        try
        {
            File.WriteAllText(path, """{"displayMode":"perAccount","maxIcons":3,"accounts":[]}""");

            AccountsConfig config = AccountsConfig.LoadOrCreateDefault(path);

            Assert.Single(config.Accounts);
            string[] badFiles = Directory.GetFiles(Path.GetDirectoryName(path)!, "accounts.bad-*.json");
            Assert.Single(badFiles);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = NewPath("accounts-roundtrip");
        try
        {
            var original = new AccountsConfig
            {
                DisplayMode = AccountDisplayMode.Binding,
                MaxIcons = 5,
                Accounts = new List<AccountEntry>
                {
                    new() { ConfigDir = null, Label = null, Enabled = true },
                    new() { ConfigDir = @"C:\Users\someone\AppData\Local\ClaudeStatusBar\accounts\1\config", Label = "Team", Enabled = false },
                },
            };

            AccountsConfig.Save(original, path);
            AccountsConfig loaded = AccountsConfig.LoadOrCreateDefault(path);

            Assert.Equal(AccountDisplayMode.Binding, loaded.DisplayMode);
            Assert.Equal(5, loaded.MaxIcons);
            Assert.Equal(2, loaded.Accounts.Count);
            Assert.Null(loaded.Accounts[0].ConfigDir);
            Assert.True(loaded.Accounts[0].Enabled);
            Assert.Equal(@"C:\Users\someone\AppData\Local\ClaudeStatusBar\accounts\1\config", loaded.Accounts[1].ConfigDir);
            Assert.Equal("Team", loaded.Accounts[1].Label);
            Assert.False(loaded.Accounts[1].Enabled);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveThenLoad_PreservesUnknownTopLevelAndPerAccountFields()
    {
        string path = NewPath("accounts-unknown-fields");
        try
        {
            File.WriteAllText(path, """
            {
              "displayMode": "perAccount",
              "maxIcons": 3,
              "futureTopLevelSetting": "keepme",
              "accounts": [
                { "configDir": null, "label": null, "enabled": true, "futurePerAccountSetting": 42 }
              ]
            }
            """);

            AccountsConfig loaded = AccountsConfig.LoadOrCreateDefault(path);
            AccountsConfig.Save(loaded, path); // re-save with no changes -- unknown fields must survive this round trip

            string savedJson = File.ReadAllText(path);
            Assert.Contains("futureTopLevelSetting", savedJson);
            Assert.Contains("keepme", savedJson);
            Assert.Contains("futurePerAccountSetting", savedJson);
            Assert.Contains("42", savedJson);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

using ClaudeStatusBar.Config;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// %LOCALAPPDATA%\ClaudeStatusBar\accounts.json (docs/multi-account.md "Configuration"): version 2
/// schema, an empty account list as a valid state, malformed-file recovery (quarantined, never
/// crashes), round-trip save/load, and unknown-field preservation. What the slots do to the list
/// (migration, reconciliation) is AccountReconcilerTests.
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
    public void Load_FirstRun_IsAnEmptyCurrentVersionConfig_AndWritesNothing()
    {
        string path = NewPath("accounts-firstrun");
        try
        {
            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Missing, load.Status);
            Assert.Equal(AccountsConfig.CurrentVersion, load.Config.Version);
            Assert.Equal(AccountDisplayMode.PerAccount, load.Config.DisplayMode);
            Assert.Equal(3, load.Config.MaxIcons);
            Assert.Empty(load.Config.Accounts);
            Assert.False(File.Exists(path)); // the caller saves once it has reconciled against the slots
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_EmptyAccountsArray_IsValid_NotQuarantined()
    {
        string path = NewPath("accounts-empty");
        try
        {
            File.WriteAllText(path, """{"version":2,"displayMode":"perAccount","maxIcons":3,"accounts":[]}""");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Loaded, load.Status);
            Assert.Empty(load.Config.Accounts);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "accounts.bad-*.json"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_NullAccounts_IsTheSameAsEmpty()
    {
        string path = NewPath("accounts-null");
        try
        {
            File.WriteAllText(path, """{"version":2,"maxIcons":3,"accounts":null}""");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Loaded, load.Status);
            Assert.NotNull(load.Config.Accounts);
            Assert.Empty(load.Config.Accounts);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_MalformedJson_IsQuarantinedNotOverwritten_AndAnEmptyConfigStandsIn()
    {
        string path = NewPath("accounts-malformed");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Quarantined, load.Status);
            Assert.Empty(load.Config.Accounts);
            Assert.False(File.Exists(path));

            // The broken file was renamed, not overwritten or deleted -- still inspectable.
            string[] badFiles = Directory.GetFiles(Path.GetDirectoryName(path)!, "accounts.bad-*.json");
            Assert.Single(badFiles);
            Assert.Contains("this is not valid json", File.ReadAllText(badFiles[0]));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_NonPositiveMaxIcons_IsInvalid_AndQuarantined()
    {
        string path = NewPath("accounts-maxicons");
        try
        {
            File.WriteAllText(path, """{"version":2,"maxIcons":0,"accounts":[]}""");

            Assert.Equal(AccountsConfigStatus.Quarantined, AccountsConfig.Load(path).Status);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "accounts.bad-*.json"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_VersionOneFile_HasVersionZero_SoMigrationRecognisesIt_AndKeepsTheLegacyConfigDir()
    {
        string path = NewPath("accounts-v1");
        try
        {
            File.WriteAllText(path, """
            {
              "displayMode": "binding",
              "maxIcons": 2,
              "accounts": [
                { "configDir": "C:\\Users\\someone\\AppData\\Local\\ClaudeStatusBar\\accounts\\1\\config", "label": "Team", "enabled": false },
                { "configDir": null, "label": null, "enabled": true }
              ]
            }
            """);

            AccountsConfig config = AccountsConfig.Load(path).Config;

            Assert.Equal(0, config.Version);
            Assert.Equal(AccountDisplayMode.Binding, config.DisplayMode);
            Assert.Equal(2, config.Accounts.Count);
            Assert.Equal(@"C:\Users\someone\AppData\Local\ClaudeStatusBar\accounts\1\config", config.Accounts[0].LegacyConfigDir);
            Assert.Equal("Team", config.Accounts[0].Label);
            Assert.False(config.Accounts[0].Enabled);
            Assert.Null(config.Accounts[1].LegacyConfigDir);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndNeverWritesAConfigDir()
    {
        string path = NewPath("accounts-roundtrip");
        try
        {
            var original = new AccountsConfig
            {
                Version = AccountsConfig.CurrentVersion,
                DisplayMode = AccountDisplayMode.Binding,
                MaxIcons = 5,
                Accounts = new List<AccountEntry>
                {
                    new() { Slot = "a1b2c3d4", Label = null, Enabled = true },
                    new() { Slot = "4", Label = "Team", Enabled = false },
                },
            };

            AccountsConfig.Save(original, path);
            AccountsConfig loaded = AccountsConfig.Load(path).Config;

            Assert.Equal(2, loaded.Version);
            Assert.Equal(AccountDisplayMode.Binding, loaded.DisplayMode);
            Assert.Equal(5, loaded.MaxIcons);
            Assert.Equal(2, loaded.Accounts.Count);
            Assert.Equal("a1b2c3d4", loaded.Accounts[0].Slot);
            Assert.True(loaded.Accounts[0].Enabled);
            Assert.Equal("4", loaded.Accounts[1].Slot);
            Assert.Equal("Team", loaded.Accounts[1].Label);
            Assert.False(loaded.Accounts[1].Enabled);

            string json = File.ReadAllText(path);
            Assert.Contains("\"version\": 2", json);
            Assert.DoesNotContain("configDir", json);
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
              "version": 2,
              "displayMode": "perAccount",
              "maxIcons": 3,
              "futureTopLevelSetting": "keepme",
              "accounts": [
                { "slot": "a1b2c3d4", "label": null, "enabled": true, "futurePerAccountSetting": 42 }
              ]
            }
            """);

            AccountsConfig loaded = AccountsConfig.Load(path).Config;
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

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Load_VersionOneFile_WithAnOutOfRangeMaxIcons_IsClamped_NotQuarantined(int maxIcons)
    {
        string path = NewPath("accounts-v1-maxicons");
        try
        {
            File.WriteAllText(path, $$"""{"maxIcons":{{maxIcons}},"accounts":[{"configDir":"C:\\Users\\someone\\x","enabled":true}]}""");

            AccountsConfigLoad load = AccountsConfig.Load(path);

            Assert.Equal(AccountsConfigStatus.Loaded, load.Status);
            Assert.Equal(1, load.Config.MaxIcons);
            Assert.Single(load.Config.Accounts); // the accounts it lists are not thrown away over a setting
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "accounts.bad-*.json"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_VersionTwoFile_WithAnOutOfRangeMaxIcons_IsStillQuarantined()
    {
        string path = NewPath("accounts-v2-maxicons");
        try
        {
            File.WriteAllText(path, """{"version":2,"maxIcons":-1,"accounts":[]}""");

            Assert.Equal(AccountsConfigStatus.Quarantined, AccountsConfig.Load(path).Status);
        }
        finally
        {
            Cleanup(path);
        }
    }
}

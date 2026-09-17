using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// &lt;configDir&gt;\.claude.json -&gt; oauthAccount (docs/multi-account.md). Every failure mode must
/// yield null, never throw: this is read on a plain poll cadence and the file can legitimately
/// be absent or mid-write at any time.
/// </summary>
public class AccountIdentityTests
{
    static string NewConfigDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteClaudeJson(string configDir, string json) =>
        File.WriteAllText(Path.Combine(configDir, ".claude.json"), json);

    [Fact]
    public void ReadFrom_FullOauthAccount_ReadsEveryField()
    {
        string dir = NewConfigDir("identity-full");
        try
        {
            WriteClaudeJson(dir, """
            {
              "oauthAccount": {
                "accountUuid": "11111111-2222-3333-4444-555555555555",
                "organizationUuid": "org-uuid",
                "organizationName": "Acme Corp",
                "organizationType": "team",
                "organizationRole": "member",
                "seatTier": "standard",
                "billingType": "organization",
                "emailAddress": "alice@example.com",
                "displayName": "Alice Example",
                "fullName": "Alice Q Example"
              }
            }
            """);

            AccountIdentity? identity = AccountIdentity.ReadFrom(dir);

            Assert.NotNull(identity);
            Assert.Equal("11111111-2222-3333-4444-555555555555", identity!.AccountUuid);
            Assert.Equal("org-uuid", identity.OrganizationUuid);
            Assert.Equal("Acme Corp", identity.OrganizationName);
            Assert.Equal("team", identity.OrganizationType);
            Assert.Equal("standard", identity.SeatTier);
            Assert.Equal("organization", identity.BillingType);
            Assert.Equal("alice@example.com", identity.EmailAddress);
            Assert.Equal("Alice Example", identity.DisplayName);
            Assert.Equal("11111111", identity.UuidPrefix);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_MissingFile_ReturnsNull()
    {
        string dir = NewConfigDir("identity-missing");
        try
        {
            Assert.Null(AccountIdentity.ReadFrom(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_MissingOauthAccountKey_ReturnsNull()
    {
        string dir = NewConfigDir("identity-nooauth");
        try
        {
            WriteClaudeJson(dir, """{"numStartups": 12, "projects": {}}""");
            Assert.Null(AccountIdentity.ReadFrom(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_MalformedJson_ReturnsNull_NeverThrows()
    {
        string dir = NewConfigDir("identity-malformed");
        try
        {
            WriteClaudeJson(dir, "{ not json at all ");
            AccountIdentity? identity = null;
            Exception? thrown = Record.Exception(() => identity = AccountIdentity.ReadFrom(dir));

            Assert.Null(thrown);
            Assert.Null(identity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_PersonalAccount_OrganizationNameAbsent_StillReadsIdentity()
    {
        string dir = NewConfigDir("identity-personal");
        try
        {
            WriteClaudeJson(dir, """
            {
              "oauthAccount": {
                "accountUuid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "emailAddress": "bob@example.com",
                "seatTier": "individual"
              }
            }
            """);

            AccountIdentity? identity = AccountIdentity.ReadFrom(dir);

            Assert.NotNull(identity);
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", identity!.AccountUuid);
            Assert.Null(identity.OrganizationName);
            Assert.Null(identity.OrganizationUuid);
            Assert.Equal("bob@example.com", identity.EmailAddress);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_EmptyAccountUuid_ReturnsNull()
    {
        string dir = NewConfigDir("identity-emptyuuid");
        try
        {
            WriteClaudeJson(dir, """{"oauthAccount": {"accountUuid": "", "emailAddress": "x@example.com"}}""");
            Assert.Null(AccountIdentity.ReadFrom(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- StateKey: the (accountUuid, organizationUuid) pair (docs/multi-account.md "Identity
    // guard"). accountUuid identifies the PERSON, not the plan -- a real Team login and a real
    // personal Max login for the same person on this machine share the SAME accountUuid and
    // differ only by organizationUuid; see the values below. ----

    [Fact]
    public void StateKey_SameAccountUuid_DifferentOrganizationUuid_ProducesDifferentKeys()
    {
        const string sharedAccountUuid = "11111111-1111-4111-8111-111111111111";
        string dirTeam = NewConfigDir("identity-team");
        string dirMax = NewConfigDir("identity-max");
        try
        {
            WriteClaudeJson(dirTeam, $$"""
            {
              "oauthAccount": {
                "accountUuid": "{{sharedAccountUuid}}",
                "organizationUuid": "org-team-uuid",
                "organizationName": "Acme AB",
                "organizationType": "claude_team"
              }
            }
            """);
            WriteClaudeJson(dirMax, $$"""
            {
              "oauthAccount": {
                "accountUuid": "{{sharedAccountUuid}}",
                "organizationUuid": "org-max-uuid",
                "organizationName": "alex@example.com's Organization",
                "organizationType": "claude_max"
              }
            }
            """);

            AccountIdentity? team = AccountIdentity.ReadFrom(dirTeam);
            AccountIdentity? max = AccountIdentity.ReadFrom(dirMax);

            Assert.NotNull(team);
            Assert.NotNull(max);
            Assert.Equal(sharedAccountUuid, team!.AccountUuid);
            Assert.Equal(sharedAccountUuid, max!.AccountUuid); // same person
            Assert.NotEqual(team.StateKey, max.StateKey); // but different plans -> different state keys
            Assert.Equal($"{sharedAccountUuid}_org-team-uuid", team.StateKey);
            Assert.Equal($"{sharedAccountUuid}_org-max-uuid", max.StateKey);
        }
        finally
        {
            Directory.Delete(dirTeam, recursive: true);
            Directory.Delete(dirMax, recursive: true);
        }
    }

    [Fact]
    public void StateKey_MissingOrganizationUuid_FallsBackToAccountUuidAlone()
    {
        string dir = NewConfigDir("identity-no-org");
        try
        {
            WriteClaudeJson(dir, """{"oauthAccount": {"accountUuid": "solo-account-uuid"}}""");
            AccountIdentity? identity = AccountIdentity.ReadFrom(dir);

            Assert.NotNull(identity);
            Assert.Null(identity!.OrganizationUuid);
            Assert.Equal("solo-account-uuid", identity.StateKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StateKeyOf_MissingOrganizationUuid_FallsBackToAccountUuidAlone()
    {
        Assert.Equal("abc", AccountIdentity.StateKeyOf("abc", null));
        Assert.Equal("abc", AccountIdentity.StateKeyOf("abc", ""));
    }

    [Fact]
    public void KeyPrefixOf_TruncatesEachIdentifierInTheCompositeKeyToEightCharacters()
    {
        // Privacy rule (AccountIdentity's doc comment): at most 8 characters of each identifier
        // may ever reach a log line -- applied to both halves of a composite StateKey.
        string key = AccountIdentity.StateKeyOf("11111111-1111-4111-8111-111111111111", "org-team-uuid-longer-than-eight-chars");
        Assert.Equal("11111111_org-team", AccountIdentity.KeyPrefixOf(key));
    }

    [Fact]
    public void KeyPrefixOf_NoOrganizationHalf_TruncatesJustTheAccountUuid()
    {
        Assert.Equal("11111111", AccountIdentity.KeyPrefixOf("11111111-1111-4111-8111-111111111111"));
    }

    [Fact]
    public void KeyPrefixOf_NullOrEmpty_ReturnsPlaceholder()
    {
        Assert.Equal("?", AccountIdentity.KeyPrefixOf(null));
        Assert.Equal("?", AccountIdentity.KeyPrefixOf(""));
    }

    [Fact]
    public void ResolveDefaultConfigDir_UsesEnvVar_WhenSet()
    {
        string? original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"D:\custom\claude-config");
            Assert.Equal(@"D:\custom\claude-config", AccountIdentity.ResolveDefaultConfigDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Fact]
    public void ResolveDefaultConfigDir_FallsBackToUserProfileDotClaude_WhenUnset()
    {
        string? original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            Assert.Equal(expected, AccountIdentity.ResolveDefaultConfigDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    // ---- ResolveDefaultIdentityDir: verified against a real installation (see the task's live
    // verification step) that %USERPROFILE%\.claude.json sits BESIDE %USERPROFILE%\.claude, not
    // inside it -- this is deliberately a different value from ResolveDefaultConfigDir above. ----

    [Fact]
    public void ResolveDefaultIdentityDir_UsesEnvVar_WhenSet()
    {
        string? original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"D:\custom\claude-config");
            Assert.Equal(@"D:\custom\claude-config", AccountIdentity.ResolveDefaultIdentityDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Fact]
    public void ResolveDefaultIdentityDir_FallsBackToBareUserProfile_NotUserProfileDotClaude_WhenUnset()
    {
        string? original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            string expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(expected, AccountIdentity.ResolveDefaultIdentityDir());
            Assert.NotEqual(AccountIdentity.ResolveDefaultConfigDir(), AccountIdentity.ResolveDefaultIdentityDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }
}

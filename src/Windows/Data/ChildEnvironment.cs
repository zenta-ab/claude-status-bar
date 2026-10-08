using ClaudeStatusBar.Config;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The ONE environment builder for every `claude` this app spawns -- the polling child, `auth
/// login` and `auth status` (docs/multi-account.md "Environment"). A child must see exactly one
/// login: the one stored in its own slot. So the builder
///
///   - pins CLAUDE_CONFIG_DIR *and* CLAUDE_SECURESTORAGE_CONFIG_DIR to the slot's config dir. The
///     second one outranks the first when choosing where credentials live, so a stray inherited
///     value would silently move every account onto one shared store while each still looked isolated;
///   - removes every variable that makes Claude Code use some OTHER credential, identity, provider
///     or endpoint than the stored OAuth login (the list below, found in the 2.1.292 binary);
///   - gives the child the slot's own agent folder as working directory, never a user repo.
///
/// The slot's config dir comes from SlotStore's path guard, so nothing outside the accounts root
/// can ever be handed to a child. The result is data (a dictionary where a null value means
/// "remove this variable"), so it is unit-tested without spawning anything.
/// </summary>
public static class ChildEnvironment
{
    public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";
    public const string SecureStorageVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR";

    /// <summary>
    /// Variables removed from the child's environment. Grouped by what they would override:
    /// a stored login (tokens, key files, descriptors), the account's identity or plan, the
    /// inference provider, the API / OAuth endpoints, Console profiles, and host-managed auth.
    /// </summary>
    public static IReadOnlyList<string> Removed { get; } = new[]
    {
        // A bearer token or refresh token instead of the stored login.
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
        "CLAUDE_CODE_OAUTH_SCOPES", "CLAUDE_CODE_OAUTH_CLIENT_ID",
        "CLAUDE_CODE_SESSION_ACCESS_TOKEN", "CLAUDE_SESSION_INGRESS_TOKEN_FILE",
        "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR", "CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR",
        "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY",

        // Another inference provider or gateway instead of claude.ai.
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_USE_MANTLE", "CLAUDE_CODE_USE_GATEWAY",
        "CLAUDE_CODE_GATEWAY_TOKEN", "CLAUDE_CODE_GATEWAY_TOKEN_FILE_DESCRIPTOR",
        "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_FOUNDRY_AUTH_TOKEN", "ANTHROPIC_AWS_API_KEY", "AWS_BEARER_TOKEN_BEDROCK",

        // A different API / OAuth endpoint, or extra headers on every request.
        "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "CLAUDE_CODE_API_BASE_URL", "CLAUDE_CODE_CUSTOM_OAUTH_URL",
        "CLAUDE_LOCAL_OAUTH_API_BASE", "CLAUDE_LOCAL_OAUTH_APPS_BASE", "CLAUDE_LOCAL_OAUTH_CONSOLE_BASE",

        // Console / workload-identity profiles, which carry their own credentials.
        "ANTHROPIC_PROFILE", "ANTHROPIC_CONFIG_DIR", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID",
        "ANTHROPIC_IDENTITY_TOKEN", "ANTHROPIC_IDENTITY_TOKEN_FILE", "ANTHROPIC_ENVIRONMENT_KEY",

        // Overrides of who the account is and which plan it has.
        "CLAUDE_CODE_ACCOUNT_UUID", "CLAUDE_CODE_ORGANIZATION_UUID", "CLAUDE_CODE_USER_EMAIL",
        "CLAUDE_CODE_SUBSCRIPTION_TYPE", "CLAUDE_CODE_RATE_LIMIT_TIER",

        // Auth handled by a host application (an SDK / IDE parent) rather than by the stored login.
        "CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH", "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH",
        "CLAUDE_CODE_HOST_AUTH_ENV_VAR", "CLAUDE_CODE_HOST_CREDS_FILE",
        "CLAUDE_BG_AUTH_SNAPSHOT_PATH", "CLAUDE_BG_CLAIM_AUTH", "CLAUDE_BG_PTY_AUTH", "CLAUDE_BG_RV_AUTH", "CLAUDE_BG_SOCKET_TOKENS_PATH",
    };

    /// <param name="Overrides">Variable to value; a null value means "remove it from the child's environment".</param>
    /// <param name="WorkingDirectory">The slot's own agent folder.</param>
    public sealed record Plan(IReadOnlyDictionary<string, string?> Overrides, string WorkingDirectory);

    /// <summary>Builds the environment and working directory for one slot. Throws if the path guard refuses the slot.</summary>
    public static Plan ForSlot(SlotStore slots, string slotId, string? agentRootOverride = null)
    {
        string configDir = slots.ConfigDirOf(slotId);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Removed) overrides[name] = null;
        overrides[ConfigDirVariable] = configDir;
        overrides[SecureStorageVariable] = configDir;

        string agentRoot = agentRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "agent");
        return new Plan(overrides, Path.Combine(agentRoot, slotId));
    }

    /// <summary>Applies a plan's overrides to a ProcessStartInfo.Environment (or any environment dictionary): sets values, removes nulls.</summary>
    public static void Apply(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string?> overrides)
    {
        foreach ((string key, string? value) in overrides)
        {
            if (value is null) environment.Remove(key);
            else environment[key] = value;
        }
    }
}

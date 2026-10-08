using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Data;

/// <summary>What `claude auth status --json` says about a slot -- only the two fields the login flow acts on.</summary>
public sealed record AuthStatus(bool LoggedIn, string? AuthMethod)
{
    /// <summary>The only login this app tracks: a claude.ai subscription. Console / API-key logins have no quota windows.</summary>
    public bool IsClaudeAiLogin => LoggedIn && string.Equals(AuthMethod, "claude.ai", StringComparison.Ordinal);
}

/// <summary>
/// Reads the output of `claude auth status --json`. That output contains the account's email, so
/// this keeps ONLY loggedIn and authMethod and the raw text is never logged, stored or returned.
/// Anything that is not a JSON object with a boolean loggedIn is "no answer" (null).
/// </summary>
public static class AuthStatusParser
{
    public static AuthStatus? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("loggedIn", out JsonElement loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;

            string? method = root.TryGetProperty("authMethod", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            return new AuthStatus(loggedIn.GetBoolean(), method);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The two `claude auth ...` commands the login flow runs (docs/multi-account.md "Login flow"),
/// both with the ONE environment builder (ChildEnvironment) so they see exactly the slot the
/// polling child will see:
///
///   `auth login --claudeai`  in a visible console the user completes in the browser;
///   `auth status --json`     headless, bounded, to learn whether the login took.
///
/// Neither ever touches a folder the path guard has not accepted. There is deliberately no
/// `auth logout` here: a logout revokes the grant on the server, which may also affect another
/// slot holding a grant for the same account (unverified), so removing an account only deletes
/// its folder.
/// </summary>
public sealed class ClaudeAuthCli : IAuthCli
{
    readonly SlotStore _slots;
    readonly Func<string> _resolveExe;
    readonly string? _agentRootOverride;

    public ClaudeAuthCli(SlotStore slots, Func<string>? resolveExe = null, string? agentRootOverride = null)
    {
        _slots = slots;
        _resolveExe = resolveExe ?? ChildProcessSpec.ResolveInstalledClaudeExe;
        _agentRootOverride = agentRootOverride;
    }

    /// <summary>
    /// Opens a visible console that prints `notice` and runs `claude auth login --claudeai` for the
    /// slot, and waits for it to end. Returns the exit code, or null when the wait was cancelled.
    /// The console is a PowerShell process only so the notice can be printed in UTF-8 and the exit
    /// code passed through.
    ///
    /// Deliberately NOT in a kill-on-close job, and never killed here: when `claude auth login`
    /// cold-starts the browser, the browser is a descendant of this console, and ending the console
    /// (or a job holding it) would close the user's browser mid-login. If the app exits while a login
    /// is under way the console is simply left running; the pending slot it is writing into is
    /// adopted by the next start's reconciliation if the login finished. There is no timeout
    /// either: the user closes the window to give up, which ends the process and the wait.
    /// </summary>
    public async Task<int?> RunLoginConsoleAsync(string slotId, string notice, CancellationToken ct)
    {
        ChildEnvironment.Plan plan = ChildEnvironment.ForSlot(_slots, slotId, _agentRootOverride);
        Directory.CreateDirectory(plan.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            Arguments = "-NoProfile -NoLogo -EncodedCommand " + EncodeScript(BuildLoginScript(_resolveExe(), notice)),
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false, // a GUI parent gives a console child its own window (the default terminal, e.g. Windows Terminal, hosts it)
        };
        ChildEnvironment.Apply(psi.Environment, plan.Overrides);
        return await RunAndWaitAsync(psi, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// `claude auth status --json` for the slot, headless, killed after `timeout`. Null when the
    /// command did not answer with something parseable. The raw output is never logged: it holds
    /// the account's email.
    /// </summary>
    public async Task<AuthStatus?> GetStatusAsync(string slotId, TimeSpan timeout, CancellationToken ct)
    {
        ChildProcessSpec spec = ChildProcessSpec.ForSlot(_slots, slotId, "auth status --json", _agentRootOverride);
        string exe = _resolveExe();
        Directory.CreateDirectory(spec.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (spec.EnvironmentOverrides is { } overrides) ChildEnvironment.Apply(psi.Environment, overrides);

        try
        {
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for claude.exe");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cts.Token); // drained so a full pipe can never stall the child
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                string text = await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return AuthStatusParser.Parse(text);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(1000); } catch { /* already gone */ }
                SafeLog.Warn($"slot {slotId}: auth status did not finish in time");
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeLog.Warn($"slot {slotId}: auth status could not run ({ex.GetType().Name})");
            return null;
        }
    }

    // ---- launching: a plain process, no job ----

    static async Task<int?> RunAndWaitAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for the login console");
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null; // stop waiting; the console and any browser it started are left alone
        }
    }

    // ---- the console script ----

    /// <summary>
    /// The script the login console runs: the notice, then `claude auth login --claudeai`, then the
    /// exit code passed through. Single quotes are doubled, so neither the notice nor an install
    /// path can break out of its literal.
    /// </summary>
    internal static string BuildLoginScript(string claudeExe, string notice)
    {
        string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        return string.Join("\n",
            "$Host.UI.RawUI.WindowTitle = 'Claude Status Bar - logga in'",
            $"Write-Host {Quote(notice)}",
            "Write-Host ''",
            $"& {Quote(claudeExe)} auth login --claudeai",
            "exit $LASTEXITCODE");
    }

    internal static string EncodeScript(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    static string ResolvePowerShell()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string full = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(full) ? full : "powershell.exe";
    }
}

/// <summary>
/// The two `claude auth ...` commands as the login flow needs them -- a seam, so the flow's ordering
/// (Flow/AccountFlowController) is tested without opening a console or a browser.
/// </summary>
public interface IAuthCli
{
    /// <summary>Runs the visible login console for the slot and waits; the exit code, or null when the wait was cancelled.</summary>
    Task<int?> RunLoginConsoleAsync(string slotId, string notice, CancellationToken ct);

    /// <summary>`auth status --json` for the slot, bounded by `timeout`; null when there was no usable answer.</summary>
    Task<AuthStatus?> GetStatusAsync(string slotId, TimeSpan timeout, CancellationToken ct);
}

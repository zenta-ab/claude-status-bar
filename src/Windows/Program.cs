using System.Linq;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Ui;

namespace ClaudeStatusBar;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
            return HandleCensus.RunSelfTest();

        if (args.Contains("--backfill-statistics"))
            return RunStatisticsBackfill();

        if (args.Contains("--rebuild-statistics"))
            return RunStatisticsRebuild();

        // UI exception boundary (Codex review Medium #15): must be set before any handle is
        // created. CatchException keeps WinForms routing exceptions to ThreadException below
        // instead of the OS-default unattended dialog/termination; the AppDomain handler
        // below is the last-resort net for exceptions off the UI thread (e.g. a thread-pool
        // continuation) that WinForms' own handler never sees.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            SafeLog.Error($"Unhandled UI thread exception (app stays alive): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SafeLog.Error($"Unhandled exception (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");

        ApplicationConfiguration.Initialize(); // reads ApplicationHighDpiMode etc from the csproj

        bool demo = args.Contains("--demo");
        string? captureStatesDir = GetArgValue(args, "--capture-states");
        string? captureIconSheet = GetArgValue(args, "--capture-icon-sheet");
        string? captureStatisticsDir = GetArgValue(args, "--capture-statistics");
        if (captureStatesDir != null) PanelForm.DiagnosticsEnabled = true; // docs/panel-v2.md's width guard: log "OVERFLOW <text>" only during this verification run

        // Codex review Low #17: the context (and everything it owns -- timers, channel,
        // panel, tray icon) is disposed here in every case, not only the ExitApp() path.
        var context = new StatusBarApplicationContext(demoMode: demo, showPanelOnStartup: args.Contains("--show-panel"));
        try
        {
            // docs/statistics.md task item 3: RunStatisticsCapture does its work synchronously
            // (DrawToBitmap never needs a running message loop the way the panel's screen-capture
            // path used to) and calls ExitApp()/ExitThread() itself before returning -- calling
            // Application.Run afterward would start a FRESH message loop with no memory of that
            // already-requested exit and hang forever, so this path deliberately skips Run
            // entirely, unlike RunAutomatedCapture (below), which defers its own exit to a timer
            // tick that only fires once Run is genuinely pumping messages.
            if (captureStatisticsDir != null)
            {
                context.RunStatisticsCapture(captureStatisticsDir);
            }
            else
            {
                if (demo && (captureStatesDir != null || captureIconSheet != null))
                    context.RunAutomatedCapture(captureStatesDir, captureIconSheet);

                Application.Run(context);
            }
        }
        finally
        {
            context.Dispose();
        }
        return 0;
    }

    static string? GetArgValue(string[] args, string name)
    {
        int idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>
    /// docs/statistics.md, decision 5: a scriptable entry point for the backfill -- re-runnable,
    /// safe against the app also running (StatisticsBackfill merges rather than overwrites).
    /// Prints per-account aggregates only, with the account key truncated to 8 characters per
    /// identifier (AccountIdentity.KeyPrefixOf) -- never a raw UUID, and nothing per-cycle.
    /// </summary>
    static int RunStatisticsBackfill()
    {
        string baseLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
        IReadOnlyList<StatisticsBackfill.AccountResult> results = StatisticsBackfill.Run(baseLogDir);

        if (results.Count == 0)
        {
            Console.WriteLine("No backfillable account directories found under " + baseLogDir);
            return 0;
        }

        foreach (StatisticsBackfill.AccountResult r in results)
        {
            string key = AccountIdentity.KeyPrefixOf(r.AccountKey);
            Console.WriteLine(
                $"{key}: cycles derived={r.CyclesDerived} (newly written={r.CyclesNewlyWritten}), " +
                $"hit ceiling={r.CyclesHitCeiling}; hourly rows derived={r.HourlyRowsDerived} " +
                $"(newly written={r.HourlyRowsNewlyWritten})");
        }
        return 0;
    }

    /// <summary>
    /// docs/statistics.md, "Codex review round (2026-09-21)": a ONE-TIME migration entry point --
    /// unlike --backfill-statistics (idempotent add-only merge, the steady-state behaviour), this
    /// discards every stale backfill-derived row (produced under the earlier, flawed rules) and
    /// re-derives the archive fresh from window-shape-*.csv (read-only) under the current rules,
    /// while preserving every live-written row (plan tier, predictions) untouched. Same
    /// account-key-truncated, aggregates-only reporting contract as --backfill-statistics.
    /// </summary>
    static int RunStatisticsRebuild()
    {
        string baseLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
        IReadOnlyList<StatisticsBackfill.AccountResult> results = StatisticsBackfill.Rebuild(baseLogDir);

        if (results.Count == 0)
        {
            Console.WriteLine("No backfillable account directories found under " + baseLogDir);
            return 0;
        }

        foreach (StatisticsBackfill.AccountResult r in results)
        {
            string key = AccountIdentity.KeyPrefixOf(r.AccountKey);
            IReadOnlyList<CycleArchiveCsv.Row> cycles = CycleArchiveCsv.ReadAll(
                Path.Combine(baseLogDir, FindAccountDir(baseLogDir, r.AccountKey), CycleArchiveCsv.FileName));
            int hits = cycles.Count(c => c.HitCeiling);
            int exact = cycles.Count(c => c.HitCeiling && !c.CeilingReachedCensored);
            int censored = cycles.Count(c => c.HitCeiling && c.CeilingReachedCensored);
            int hourlyRows = HourlyRollupCsv.FindYearFiles(Path.Combine(baseLogDir, FindAccountDir(baseLogDir, r.AccountKey)))
                .Sum(f => HourlyRollupCsv.ReadAll(f).Count);
            Console.WriteLine(
                $"{key}: cycles={cycles.Count}, hit ceiling={hits} (exact={exact}, censored={censored}), hourly rows={hourlyRows}");
        }
        return 0;
    }

    static string FindAccountDir(string baseLogDir, string accountKey) =>
        Directory.EnumerateDirectories(baseLogDir).Select(Path.GetFileName).First(n => n == accountKey)!;
}

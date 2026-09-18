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
        if (captureStatesDir != null) PanelForm.DiagnosticsEnabled = true; // docs/panel-v2.md's width guard: log "OVERFLOW <text>" only during this verification run

        // Codex review Low #17: the context (and everything it owns -- timers, channel,
        // panel, tray icon) is disposed here in every case, not only the ExitApp() path.
        var context = new StatusBarApplicationContext(demoMode: demo, showPanelOnStartup: args.Contains("--show-panel"));
        try
        {
            if (demo && (captureStatesDir != null || captureIconSheet != null))
                context.RunAutomatedCapture(captureStatesDir, captureIconSheet);

            Application.Run(context);
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
}

namespace ClaudeStatusBar.Diagnostics;

/// <summary>
/// Console.WriteLine wrapper used by every runtime/lifecycle path in this app
/// (Codex review Medium #15): this is a WinExe with no console attached, and even
/// the existing diagnostic Console.WriteLine calls can throw -- a broken or
/// redirected stdout is enough -- which must never be able to take the app down.
/// Every write goes through here and swallows any failure.
/// </summary>
public static class SafeLog
{
    public static void Info(string message) => Write(message);

    public static void Warn(string message) => Write($"WARN: {message}");

    public static void Error(string message) => Write($"ERROR: {message}");

    static void Write(string message)
    {
        try { Console.WriteLine(message); }
        catch { /* diagnostic output must never crash the app */ }
    }
}

using System.Text;
using System.Text.Json;

// A stand-in for claude.exe's `-p --input-format stream-json --output-format stream-json
// --verbose` control-protocol behaviour, driven entirely by command-line flags so
// ClaudeCliChannelSupervisor integration tests can point ChildProcessSpec at this instead of
// the real binary. Every mode below corresponds to one of the failure shapes the task's
// TESTABILITY section calls out.
//
// I/O deliberately goes through explicit UTF8-wrapped raw streams (Console.OpenStandardInput/
// Output), never Console.In/Console.Out: Console's own TextReader/TextWriter pick an ambient
// encoding (OS codepage) that does not reliably match what the .NET parent process wrote via
// ProcessStartInfo.StandardInputEncoding = Encoding.UTF8 -- measured to corrupt the very first
// byte of the very first line into a single mojibake character, which made every test here
// look like "the child never responds" regardless of --mode. Being explicit about the
// encoding on both ends is the correct fix, not a workaround.
//
//   --mode=normal                  responds to every control_request with a normal get_usage
//                                   control_response (the default if --mode is omitted).
//   --mode=die-immediately         exits (code 0) before reading anything.
//   --mode=die-after --count=N     responds normally to the first N requests, then exits.
//   --mode=hang                    never reads stdin and never writes anything; blocks until killed.
//   --mode=null-response           responds to every request with {"type":"control_response","response":null}
//                                   (Codex review High #6's exact reproduction case).
//   --mode=null-response-then-normal   writes ONE unsolicited null-response line immediately on
//                                   start, then behaves like --mode=normal -- proves the stdout
//                                   pump survives a bad envelope rather than dying on it.
//   --mode=oversized-line          writes a single ~5MB line with NO trailing newline, then hangs
//                                   (Codex review High #7's reproduction case).
//   --mode=die-once --marker=PATH  dies immediately the first time (and creates PATH); any later
//                                   launch that finds PATH already there behaves like --mode=normal.
//                                   Simulates "the child died and a relaunch works" for the
//                                   supervisor's relaunch-with-backoff test.
//   --mode=not-logged-in           answers every request with the real "no subscription login" shape
//                                   (subscription_type null, rate_limits_available false, rate_limits null) --
//                                   what a child with an expired or missing login says (docs/multi-account.md
//                                   "NeedsLogin").
//   --mode=not-logged-in-once --marker=PATH   the first launch behaves like not-logged-in (and creates PATH);
//                                   any later launch that finds PATH behaves like --mode=normal -- a stale
//                                   process whose replacement is fine.
//   --mode=not-logged-in-until-marker --marker=PATH   answers not-logged-in to every request until PATH exists, then
//                                   normally -- an expired login that the user then renews.
//   --launch-log=PATH              (any mode) appends one line to PATH at every start, so a test can count launches.
//   --on-eof-marker=PATH           (request-loop modes) creates PATH when stdin reaches EOF, i.e. when the parent
//                                   closed it -- proves a stop was graceful rather than a kill.
//   --mode=slow --delay-ms=N       behaves like --mode=normal, but sleeps N ms after reading each
//                                   request before writing its response -- gives a test a
//                                   deterministic window in which a poll is genuinely in flight,
//                                   e.g. AccountRuntimeTests' forced-refresh concurrency guard.
string mode = "normal";
int count = int.MaxValue;
string? marker = null;
int delayMs = 0;
string? launchLog = null;
string? onEofMarker = null;

foreach (string arg in args)
{
    if (arg.StartsWith("--mode=", StringComparison.Ordinal)) mode = arg["--mode=".Length..];
    else if (arg.StartsWith("--count=", StringComparison.Ordinal)) int.TryParse(arg["--count=".Length..], out count);
    else if (arg.StartsWith("--marker=", StringComparison.Ordinal)) marker = arg["--marker=".Length..];
    else if (arg.StartsWith("--delay-ms=", StringComparison.Ordinal)) int.TryParse(arg["--delay-ms=".Length..], out delayMs);
    else if (arg.StartsWith("--launch-log=", StringComparison.Ordinal)) launchLog = arg["--launch-log=".Length..];
    else if (arg.StartsWith("--on-eof-marker=", StringComparison.Ordinal)) onEofMarker = arg["--on-eof-marker=".Length..];
}

if (launchLog != null) File.AppendAllText(launchLog, "launch" + Environment.NewLine);

// `auth status --json` / `auth login --claudeai` stand-ins (ClaudeAuthCli tests). FAKE_AUTH_DUMP
// names a file that receives what this process was started with -- cwd, the two config variables'
// values, and the NAMES (never values) of every environment variable present -- so a test can prove
// the environment builder was applied to a real child process. FAKE_AUTH_BEHAVIOUR picks the answer.
if (args.Length >= 2 && args[0] == "auth")
{
    string? dump = Environment.GetEnvironmentVariable("FAKE_AUTH_DUMP");
    if (dump != null)
    {
        var sbDump = new StringBuilder();
        sbDump.AppendLine("pid=" + Environment.ProcessId);
        sbDump.AppendLine("cwd=" + Environment.CurrentDirectory);
        sbDump.AppendLine("args=" + string.Join(' ', args));
        sbDump.AppendLine("CLAUDE_CONFIG_DIR=" + Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
        sbDump.AppendLine("CLAUDE_SECURESTORAGE_CONFIG_DIR=" + Environment.GetEnvironmentVariable("CLAUDE_SECURESTORAGE_CONFIG_DIR"));
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            sbDump.AppendLine("env:" + e.Key);
        File.WriteAllText(dump, sbDump.ToString());
    }

    string behaviour = Environment.GetEnvironmentVariable("FAKE_AUTH_BEHAVIOUR") ?? "ok";
    if (behaviour == "hang") Thread.Sleep(Timeout.Infinite);

    if (args[1] == "status")
    {
        switch (behaviour)
        {
            case "garbage": Console.Out.WriteLine("this is not json"); return 0;
            case "none": Console.Out.WriteLine("""{"loggedIn": false, "authMethod": "none"}"""); return 1;
            case "console": Console.Out.WriteLine("""{"loggedIn": true, "authMethod": "console", "email": "alex@example.com"}"""); return 0;
            default: Console.Out.WriteLine("""{"loggedIn": true, "authMethod": "claude.ai", "email": "alex@example.com"}"""); return 0;
        }
    }

    if (args[1] == "login") return int.TryParse(Environment.GetEnvironmentVariable("FAKE_AUTH_LOGIN_EXIT"), out int loginExit) ? loginExit : 0;
    return 2;
}

if (mode == "die-once")
{
    if (marker != null && File.Exists(marker))
    {
        mode = "normal"; // a previous launch already left its marker: behave normally this time
    }
    else
    {
        if (marker != null) File.WriteAllText(marker, "died");
        return 0;
    }
}

if (mode == "not-logged-in-once")
{
    if (marker != null && File.Exists(marker))
    {
        mode = "normal";
    }
    else
    {
        if (marker != null) File.WriteAllText(marker, "first launch answered not-logged-in");
        mode = "not-logged-in";
    }
}

var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8NoBom, detectEncodingFromByteOrderMarks: true);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = false };

switch (mode)
{
    case "die-immediately":
        return 0;

    case "hang":
        Thread.Sleep(Timeout.Infinite);
        return 0;

    case "oversized-line":
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"noise\",\"pad\":\"");
        sb.Append('x', 5 * 1024 * 1024); // 5MB > LineFramer.MaxRecordChars (4M chars)
        stdout.Write(sb.ToString()); // deliberately no trailing newline
        stdout.Flush();
        Thread.Sleep(Timeout.Infinite); // stay alive so the parent observes the overflow, not just EOF
        return 0;

    case "null-response-then-normal":
        stdout.WriteLine("""{"type":"control_response","response":null}""");
        stdout.Flush();
        return RunRequestLoop(stdin, stdout, "normal", count);

    case "die-after":
    case "normal":
    case "null-response":
    case "not-logged-in":
    case "not-logged-in-until-marker":
    case "slow":
        return RunRequestLoop(stdin, stdout, mode, count, delayMs, onEofMarker, marker);

    default:
        Console.Error.WriteLine($"FakeClaudeChild: unknown --mode={mode}");
        return 1;
}

static int RunRequestLoop(StreamReader stdin, StreamWriter stdout, string mode, int dieAfter, int delayMs = 0, string? onEofMarker = null, string? marker = null)
{
    int served = 0;
    string? line;
    while ((line = stdin.ReadLine()) != null)
    {
        if (mode == "die-after" && served >= dieAfter) return 0;

        string? requestId = TryExtractRequestId(line);
        if (requestId is null) continue; // not a control_request we understand; ignore

        if (mode == "slow" && delayMs > 0) Thread.Sleep(delayMs);

        string response = mode switch
        {
            "null-response" => """{"type":"control_response","response":null}""",
            "not-logged-in" => BuildNotLoggedInResponse(requestId),
            "not-logged-in-until-marker" => marker != null && File.Exists(marker) ? BuildUsageResponse(requestId) : BuildNotLoggedInResponse(requestId),
            _ => BuildUsageResponse(requestId),
        };

        stdout.WriteLine(response);
        stdout.Flush();
        served++;
    }
    if (onEofMarker != null) File.WriteAllText(onEofMarker, "stdin closed");
    return 0;
}

static string? TryExtractRequestId(string line)
{
    try
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("request_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static string BuildNotLoggedInResponse(string requestId) =>
    "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\"," +
    "\"response\":{\"session\":{\"total_cost_usd\":0,\"total_api_duration_ms\":0,\"total_duration_ms\":488,\"total_lines_added\":0,\"total_lines_removed\":0,\"model_usage\":{}}," +
    "\"subscription_type\":null,\"rate_limits_available\":false,\"rate_limits\":null,\"behaviors\":null}}}";

static string BuildUsageResponse(string requestId) =>
    "{\"type\":\"control_response\",\"response\":{\"request_id\":\"" + requestId + "\"," +
    "\"subtype\":\"success\"," +
    "\"five_hour\":{\"utilization\":42,\"resets_at\":\"" + Iso(TimeSpan.FromHours(3)) + "\"}," +
    "\"seven_day\":{\"utilization\":13.5,\"resets_at\":\"" + Iso(TimeSpan.FromDays(5)) + "\"}}}";

// Reset times must be relative to now: the model rejects resets_at outside [now - 1 d, now + 8 d]
// as garbled, so hardcoded dates turn the whole suite red the moment the calendar passes them.
static string Iso(TimeSpan ahead) =>
    (DateTimeOffset.UtcNow + ahead).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00");

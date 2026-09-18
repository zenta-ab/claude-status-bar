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
//   --mode=slow --delay-ms=N       behaves like --mode=normal, but sleeps N ms after reading each
//                                   request before writing its response -- gives a test a
//                                   deterministic window in which a poll is genuinely in flight,
//                                   e.g. AccountRuntimeTests' forced-refresh concurrency guard.
string mode = "normal";
int count = int.MaxValue;
string? marker = null;
int delayMs = 0;

foreach (string arg in args)
{
    if (arg.StartsWith("--mode=", StringComparison.Ordinal)) mode = arg["--mode=".Length..];
    else if (arg.StartsWith("--count=", StringComparison.Ordinal)) int.TryParse(arg["--count=".Length..], out count);
    else if (arg.StartsWith("--marker=", StringComparison.Ordinal)) marker = arg["--marker=".Length..];
    else if (arg.StartsWith("--delay-ms=", StringComparison.Ordinal)) int.TryParse(arg["--delay-ms=".Length..], out delayMs);
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
    case "slow":
        return RunRequestLoop(stdin, stdout, mode, count, delayMs);

    default:
        Console.Error.WriteLine($"FakeClaudeChild: unknown --mode={mode}");
        return 1;
}

static int RunRequestLoop(StreamReader stdin, StreamWriter stdout, string mode, int dieAfter, int delayMs = 0)
{
    int served = 0;
    string? line;
    while ((line = stdin.ReadLine()) != null)
    {
        if (mode == "die-after" && served >= dieAfter) return 0;

        string? requestId = TryExtractRequestId(line);
        if (requestId is null) continue; // not a control_request we understand; ignore

        if (mode == "slow" && delayMs > 0) Thread.Sleep(delayMs);

        string response = mode == "null-response"
            ? """{"type":"control_response","response":null}"""
            : BuildUsageResponse(requestId);

        stdout.WriteLine(response);
        stdout.Flush();
        served++;
    }
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

static string BuildUsageResponse(string requestId) =>
    "{\"type\":\"control_response\",\"response\":{\"request_id\":\"" + requestId + "\"," +
    "\"subtype\":\"success\"," +
    "\"five_hour\":{\"utilization\":42,\"resets_at\":\"" + Iso(TimeSpan.FromHours(3)) + "\"}," +
    "\"seven_day\":{\"utilization\":13.5,\"resets_at\":\"" + Iso(TimeSpan.FromDays(5)) + "\"}}}";

// Reset times must be relative to now: the model rejects resets_at outside [now - 1 d, now + 8 d]
// as garbled, so hardcoded dates turn the whole suite red the moment the calendar passes them.
static string Iso(TimeSpan ahead) =>
    (DateTimeOffset.UtcNow + ahead).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00");

import ClaudeQuotaCore
import Foundation

// M0 (docs/mac-port.md, "Phase 2 — milestones"): spawn the child, send get_usage, print
// utilization and reset times. This is Phase 1's evidence turned into code -- the three findings
// that changed the design are all exercised here, and the output is shaped so a regression in any
// of them is visible rather than silent.
//
//   quotaprobe                 one poll against the default login
//   quotaprobe --poll 10 15    10 polls, 15 s apart (what the fingerprint evidence was gathered with)
//   quotaprobe --config-dir D  poll the account living in config directory D
//   quotaprobe --no-skip       send get_usage WITHOUT skip_behaviors, to compare latency

// A long `--poll` run is meant to be watched, and is usually piped to a file or a pager. Swift's
// `print` block-buffers whenever stdout is not a terminal, so without this the tool looks frozen
// for minutes and then emits everything at once.
setvbuf(stdout, nil, _IONBF, 0)

struct Options {
    var pollCount = 1
    var interval: TimeInterval = 15
    var configDirectory: String?
    var skipBehaviors = true
}

func parseArguments() -> Options {
    var options = Options()
    var arguments = Array(CommandLine.arguments.dropFirst())
    while let argument = arguments.first {
        arguments.removeFirst()
        switch argument {
        case "--poll":
            if let n = arguments.first.flatMap(Int.init) { options.pollCount = n; arguments.removeFirst() }
            if let s = arguments.first.flatMap(Double.init) { options.interval = s; arguments.removeFirst() }
        case "--config-dir":
            if let d = arguments.first { options.configDirectory = d; arguments.removeFirst() }
        case "--no-skip":
            options.skipBehaviors = false
        case "-h", "--help":
            print("""
            quotaprobe — M0 protocol probe for the macOS port

              --poll <n> [seconds]   poll n times, `seconds` apart (default 1, 15 s)
              --config-dir <path>    use this CLAUDE_CONFIG_DIR instead of the default login
              --no-skip              omit skip_behaviors (slower; for latency comparison)
            """)
            exit(0)
        default:
            FileHandle.standardError.write(Data("unknown argument: \(argument)\n".utf8))
            exit(2)
        }
    }
    return options
}

let options = parseArguments()

// Containment mechanism 3 (docs/mac-port.md §6) now runs inside ControlChannel.launch, so every
// entry point gets it. Sweeping the whole agent directory here additionally clears records left
// by OTHER slots, which a single-slot launch would not touch.
let swept = ChildProcess.sweepOrphans()
if !swept.isEmpty {
    print("swept \(swept.count) orphaned claude process(es) from a previous run: \(swept.map(\.description))")
}

let spec = ChildProcessSpec.forAccount(
    slot: options.configDirectory == nil ? "default" : "probe",
    configDirectory: options.configDirectory)

print("binary       \(spec.resolveExecutablePath())")
print("working dir  \(spec.workingDirectory)")
print("config dir   \(spec.environmentOverrides["CLAUDE_CONFIG_DIR"] ?? "(inherited — Claude Code's own default login)")")
print("arguments    \(spec.arguments.joined(separator: " "))")

let channel: ControlChannel
do {
    let started = Date()
    channel = try ControlChannel.launch(spec)
    print(String(format: "spawned      pid %d (own session/process group) in %.0f ms\n",
                 channel.childPid, Date().timeIntervalSince(started) * 1000))
} catch {
    FileHandle.standardError.write(Data("launch failed: \(error)\n".utf8))
    exit(1)
}

// Terminate the child on Ctrl-C too, not just on the normal path.
let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
interrupt.setEventHandler { channel.shutdown(); exit(130) }
interrupt.resume()
signal(SIGINT, SIG_IGN)

print("  #  time          latency  source        session                                        weekly")
var previousFingerprint: String?
var distinctFingerprints = Set<String>()
var repeatedFingerprints = 0

let clock = DateFormatter()
clock.dateFormat = "HH:mm:ss.SSS"
clock.timeZone = TimeZone(identifier: "UTC")

for poll in 1...options.pollCount {
    do {
        let (result, latency) = try channel.usageSnapshot(skipBehaviors: options.skipBehaviors)
        let stamp = clock.string(from: Date())
        if let snapshot = result.snapshot {
            let fingerprint = snapshot.sessionResetsAt ?? "-"
            if !distinctFingerprints.insert(fingerprint).inserted { repeatedFingerprints += 1 }
            let repeated = fingerprint == previousFingerprint ? " =" : "  "
            previousFingerprint = fingerprint

            let session = String(format: "%5.1f %% → %@%@",
                                 snapshot.sessionUtilization ?? -1, fingerprint, repeated)
            let weekly = snapshot.weeklyUtilization.map {
                String(format: "%5.1f %% → %@", $0, snapshot.weeklyResetsAt ?? "-")
            } ?? "   (no weekly node)"
            print(String(format: "%3d  %@  %6.0f ms  %-12s  %@   %@",
                         poll, stamp, latency * 1000,
                         (result.snapshot!.source.rawValue as NSString).utf8String!,
                         session, weekly))
            if let error = result.error { print("       note: \(error)") }
            if poll == 1 {
                print("       subscription_type: \(snapshot.subscriptionType ?? "(none)")")
            }
        } else {
            print(String(format: "%3d  %@  %7.0f ms  — %@", poll, stamp, latency * 1000, result.error ?? "no snapshot"))
        }
    } catch {
        print("\(poll): \(error)")
        break
    }
    if poll < options.pollCount { Thread.sleep(forTimeInterval: options.interval) }
}

if options.pollCount > 1 {
    print("""

    \(distinctFingerprints.count) distinct session fingerprints over \(options.pollCount) polls \
    (\(repeatedFingerprints) repeats).
    Repeats are the ~5 min server-side hold; novel values in between are live fetches whose
    microseconds differ per reply — docs/forecast-and-states.md, "Re-measured on macOS".
    """)
}

channel.shutdown()
print("child terminated.")

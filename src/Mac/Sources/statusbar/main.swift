import AppKit
import ClaudeQuotaCore
import ClaudeStatusBarUI
import Foundation

// A live status bar: one `claude` child, one `QuotaModel` and one menu-bar icon per configured
// account (docs/multi-account.md).
//
//   statusbar                  poll on the model's own cadence (the spec's table)
//   statusbar --interval 60    force a fixed cadence instead, for watching it work
//
// Polling follows `QuotaModel.nextPollDelay`, and a separate 1 Hz tick runs `evaluate` — the
// freshness ladder is specified to run on the always-running UI timer precisely so that a hung
// poll can never leave a confident number frozen on screen.
setvbuf(stdout, nil, _IONBF, 0)

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

var fixedInterval: TimeInterval?
var arguments = Array(CommandLine.arguments.dropFirst())
while let argument = arguments.first {
    arguments.removeFirst()
    switch argument {
    case "--interval":
        if let value = arguments.first.flatMap(Double.init) { fixedInterval = value; arguments.removeFirst() }
    case "-h", "--help":
        print("""
        statusbar — live menu-bar quota, one icon per account

          --interval <seconds>   force a fixed poll cadence (default: the model's own)

        Accounts live in ~/Library/Application Support/ClaudeStatusBar/accounts.json.
        Add one with scripts/add-account.sh.
        """)
        exit(0)
    default:
        FileHandle.standardError.write(Data("unknown argument: \(argument)\n".utf8))
        exit(2)
    }
}

/// Monotonic milliseconds. Every duration in the model is monotonic by contract (round-1
/// decision 6) so a clock change cannot move a deadline or a verdict.
func monotonicMs() -> Int64 { Int64(DispatchTime.now().uptimeNanoseconds / 1_000_000) }

/// A rolling diagnostic log.
///
/// Launched from a terminal the app prints to stdout; launched as a bundle — which is how it
/// actually runs — that output goes nowhere. An "!" that appeared after two days then had to be
/// reconstructed from the window-shape CSVs, because nothing recorded what the app itself
/// believed. One line per state change is cheap and makes the next one answerable.
///
/// It records verdicts and freshness, never response bodies: this file must stay safe to read
/// out loud.
enum DiagnosticLog {
    private static let maximumBytes = 2 * 1024 * 1024
    private static let url: URL = {
        let directory = ChildProcessSpec.applicationSupportDirectory().appendingPathComponent("logs")
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent("statusbar.log")
    }()

    static func write(_ line: String) {
        let stamp = ISO8601DateFormatter().string(from: Date())
        let entry = "\(stamp)  \(line)\n"
        print(line)   // still useful when run from a terminal
        rotateIfNeeded()
        if !FileManager.default.fileExists(atPath: url.path) {
            try? Data(entry.utf8).write(to: url)
            return
        }
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data(entry.utf8))
    }

    private static func rotateIfNeeded() {
        guard let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int,
              size > maximumBytes else { return }
        try? FileManager.default.removeItem(at: url.appendingPathExtension("1"))
        try? FileManager.default.moveItem(at: url, to: url.appendingPathExtension("1"))
    }
}

/// One account's child, model, icon and last reading.
@MainActor
final class AccountRuntime {
    let slot: String
    let configDirectory: String?
    let labelOverride: String?
    let slotNumber: Int
    let controller: StatusItemController
    let panel = PanelController()
    let model = QuotaModel()
    private(set) var lastView: QuotaView = .initial
    var identity: AccountIdentity?
    var subscriptionType: String?
    var label: String
    var nextPollAt: Date = .distantPast
    private var channel: ControlChannel?
    private var warmStarted = false
    /// Per-identity history, so a restart does not begin cold. Keyed on the account+organisation
    /// pair — one person's Team seat and personal Max seat must never share a file.
    private var log: WindowShapeLog?

    init(slot: String, slotNumber: Int, configDirectory: String?, labelOverride: String?) {
        self.slot = slot
        self.slotNumber = slotNumber
        self.configDirectory = configDirectory
        self.labelOverride = labelOverride
        self.identity = AccountIdentity.read(configDirectory: configDirectory)
        // Deliberately NOT the full ladder yet: `subscription_type` only arrives with the first
        // poll, and resolving without it skips past the plan step to the email local part — so a
        // personal account would flash the user's email address in the menu bar before settling.
        self.label = labelOverride?.nilIfEmpty ?? "Konto \(slotNumber)"
        controller = StatusItemController(
            params: QuotaIconParams(state: .measuring, freshness: .unknown),
            toolTip: "\(label) — ansluter…")
        controller.menuHeader = [label, "Ansluter…"]
        controller.onRefresh = { [weak self] in self?.poll() }

        // Clicking the icon opens the panel (docs/panel-v2.md); the menu stays on right-click.
        panel.contentProvider = { [weak self] in
            guard let self else { return ("", .initial, []) }
            return (self.label, self.lastView, self.otherAccountRows())
        }
        panel.onSelectAccount = { index in
            // Switching accounts from the panel: open that account's own panel instead.
            guard index >= 0, index < Runtime.accounts.count else { return }
            let target = Runtime.accounts[index]
            guard let button = target.controller.item.button else { return }
            self.panel.close()
            target.panel.show(relativeTo: button)
        }
        controller.onPrimaryClick = { [weak self] button in
            self?.panel.toggle(relativeTo: button)
        }
    }

    /// The compact rows for every OTHER account (docs/multi-account.md, "Display").
    func otherAccountRows() -> [OtherAccountRow] {
        Runtime.accounts.enumerated().compactMap { index, other in
            guard other !== self else { return nil }
            return PanelText.composeOtherAccountRow(accountIndex: index, label: other.label,
                                                    view: other.lastView, now: Date(),
                                                    timeZone: .current)
        }
    }

    // MARK: - Polling

    func pollIfDue(now: Date) {
        guard now >= nextPollAt else { return }
        poll()
    }

    func poll() {
        let utcNow = Date()
        let mono = monotonicMs()

        if channel == nil || channel?.isHealthy != true {
            channel?.shutdown()
            channel = nil
            let spec = ChildProcessSpec.forAccount(slot: slot, configDirectory: configDirectory)
            do {
                channel = try ControlChannel.launch(spec)
            } catch {
                model.ingestFailure("kan inte starta claude: \(error)", utcNow: utcNow, monoMs: mono)
                scheduleNextPoll(from: utcNow)
                return
            }
        }
        guard let channel else { return }

        do {
            let (result, latency) = try channel.usageSnapshot()
            if let snapshot = result.snapshot {
                // Warm start must run BEFORE the first ingest of that same snapshot: replay
                // reconstructs history older than the live sample, and the tracker's dt <= 0
                // guard would otherwise drop every replayed row as out of order. No CSV history
                // is written yet on macOS, so this is a no-op until that lands.
                // The identity is only knowable once a login has been read, so the log is opened
                // on the first usable poll rather than at construction.
                if log == nil {
                    identity = identity ?? AccountIdentity.read(configDirectory: configDirectory)
                    log = WindowShapeLog.forAccount(stateKey: identity?.stateKey)
                    log?.prune()
                }
                if !warmStarted {
                    warmStarted = true
                    // Must run BEFORE ingesting this same snapshot (see above).
                    model.warmStart(snapshot, utcNow: utcNow,
                                    rows: log?.rowsForWarmStart(now: utcNow) ?? [])
                }
                if subscriptionType == nil, let type = snapshot.subscriptionType {
                    subscriptionType = type
                    identity = AccountIdentity.read(configDirectory: configDirectory) ?? identity
                    label = AccountLabel.resolve(override: labelOverride, identity: identity,
                                                 subscriptionType: type, slotNumber: slotNumber)
                }
                model.ingest(snapshot, utcNow: utcNow, monoMs: mono)
                log?.append(snapshot, at: utcNow, monoMs: mono, latencyMs: latency * 1000)
            } else {
                model.ingestFailure(result.error ?? "inget svar", utcNow: utcNow, monoMs: mono)
                DiagnosticLog.write("\(label): poll unusable — \(result.error ?? "inget svar")")
            }
        } catch {
            model.ingestFailure("\(error)", utcNow: utcNow, monoMs: mono)
            DiagnosticLog.write("\(label): poll failed — \(error)")
        }
        scheduleNextPoll(from: utcNow)
    }

    private func scheduleNextPoll(from utcNow: Date) {
        let delay = fixedInterval ?? model.nextPollDelay(utcNow: utcNow)
        nextPollAt = utcNow.addingTimeInterval(delay)
    }

    // MARK: - Rendering

    /// Runs on the 1 Hz tick, not only after a poll — that is the whole point of the freshness
    /// ladder: a hung poll must never leave a confident number frozen on screen.
    @discardableResult
    func render() -> QuotaView {
        let view = model.evaluate(utcNow: Date(), monoMs: monotonicMs())
        lastView = view

        controller.update(QuotaIconParams(
            weeklyFraction: (view.weekly.usedPct ?? 0) / 100,
            sessionFraction: (view.session.usedPct ?? 0) / 100,
            weeklyForecastFraction: (view.weekly.projectedPctAtReset ?? 0) / 100,
            sessionForecastFraction: (view.session.projectedPctAtReset ?? 0) / 100,
            state: view.iconSeverity,
            freshness: view.freshness))

        controller.item.button?.toolTip = Self.tooltip(label: label, view: view)
        controller.menuHeader = Self.menuLines(label: label, view: view)
        // The status box's countdowns tick at 1 Hz, so an open panel redraws with them.
        if panel.isOpen { panel.refresh() }
        return view
    }

    static func tooltip(label: String, view: QuotaView) -> String {
        switch view.freshness {
        case .unknown: return "\(label) — kan inte läsa kvoten"
        case .stale: return "\(label) — datan kan vara inaktuell · \(shortSummary(view))"
        case .live: return "\(label) · \(shortSummary(view))"
        }
    }

    static func shortSummary(_ view: QuotaView) -> String {
        let session = view.session.usedPct.map { String(format: "session %.0f %%", $0) } ?? "ingen aktiv session"
        let weekly = view.weekly.usedPct.map { String(format: "vecka %.0f %%", $0) } ?? "vecka –"
        return "\(session) · \(weekly)"
    }

    static func menuLines(label: String, view: QuotaView) -> [String] {
        var lines = [label]
        lines.append(windowLine("Session", view.session))
        lines.append(windowLine("Vecka", view.weekly))
        switch view.freshness {
        case .live: break
        case .stale: lines.append("⚠︎ Datan kan vara inaktuell")
        case .unknown: lines.append("⚠︎ Kan inte läsa kvoten")
        }
        if let error = view.error { lines.append(error) }
        return lines
    }

    /// One window, compact, for the console trace.
    static func windowDebug(_ window: WindowView) -> String {
        let used = window.usedPct.map { String(format: "%.0f%%", $0) } ?? "–"
        if let reason = window.measuringReason { return "\(used) \(window.state) (\(reason))" }
        let shortfall = window.shortfallMinutes > 0 ? String(format: " sf=%.0fm", window.shortfallMinutes) : ""
        let rate = window.ratePctPerMin.map { String(format: " r=%.3f", $0) } ?? ""
        return "\(used) \(window.state)\(rate)\(shortfall)"
    }

    static func windowLine(_ name: String, _ window: WindowView) -> String {
        guard let used = window.usedPct else { return "\(name): –" }
        let base = String(format: "%@: %.0f %% använt", name, used)
        if let reason = window.measuringReason { return "\(base) · \(reason)" }
        if let projected = window.projectedPctAtReset {
            return base + String(format: " → ~%.0f %% vid reset", min(projected, 100))
        }
        return base
    }

    func shutdown() { channel?.shutdown() }
}

@MainActor
enum Runtime {
    static var accounts: [AccountRuntime] = []
    static var timer: Timer?
    static var lastPrinted = ""
}

@MainActor
func settleLabels() {
    let resolved = AccountLabel.disambiguate(
        Runtime.accounts.map(\.label),
        plans: Runtime.accounts.map(\.subscriptionType),
        emails: Runtime.accounts.map { $0.identity?.emailAddress })
    for (runtime, label) in zip(Runtime.accounts, resolved) where runtime.label != label {
        runtime.label = label
    }
}

@MainActor
func start() {
    let config = AccountsConfig.load()
    let enabled = Array(config.enabledAccounts.prefix(max(1, config.maxIcons)))
    DiagnosticLog.write("started — \(enabled.count) enabled account(s), "
        + "accounts.json at \(AccountsConfig.fileURL.path)")

    for (index, account) in enabled.enumerated() {
        let slot = account.configDir == nil ? "default" : "\(index)"
        Runtime.accounts.append(AccountRuntime(slot: slot, slotNumber: index + 1,
                                               configDirectory: account.configDir,
                                               labelOverride: account.label))
        let location = account.configDir.map { $0.replacingOccurrences(of: NSHomeDirectory(), with: "~") }
            ?? "(Claude Code's own default login)"
        DiagnosticLog.write("  [\(index)] \(location)")
    }

    for runtime in Runtime.accounts { runtime.poll() }
    settleLabels()
    tick()

    // 1 Hz: the freshness ladder and the grace cap are specified to be evaluated on this tick,
    // independently of whether a poll succeeded.
    Runtime.timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
        MainActor.assumeIsolated { tick() }
    }
}

@MainActor
func tick() {
    let now = Date()
    for runtime in Runtime.accounts { runtime.pollIfDue(now: now) }

    var summaries: [String] = []
    for runtime in Runtime.accounts {
        let view = runtime.render()
        // Per-window, not just the icon severity: "why is it red" is the first question, and
        // max(session, weekly) alone cannot answer it.
        summaries.append("\(runtime.label): \(view.iconSeverity) ["
            + "S \(AccountRuntime.windowDebug(view.session)) | V \(AccountRuntime.windowDebug(view.weekly))] "
            + "\(view.freshness)")
    }
    // Print only on change, so a 1 Hz tick does not fill the log.
    let line = summaries.joined(separator: "   |   ")
    if line != Runtime.lastPrinted {
        Runtime.lastPrinted = line
        DiagnosticLog.write(line)
    }
}

// Ctrl-C must take every child with it, not leave them for the next run's sweep.
let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
interrupt.setEventHandler {
    MainActor.assumeIsolated { for runtime in Runtime.accounts { runtime.shutdown() } }
    exit(130)
}
interrupt.resume()
signal(SIGINT, SIG_IGN)

DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { MainActor.assumeIsolated { start() } }
app.run()

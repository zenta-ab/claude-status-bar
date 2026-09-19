import Foundation
import Testing

@testable import ClaudeQuotaCore

@Suite("ChildProcessSpec")
struct ChildProcessSpecTests {

    @Test("the native install wins over Homebrew and PATH")
    func prefersNativeInstall() {
        let path = ChildProcessSpec.resolveClaudeExecutable(
            home: "/home-x", pathVariable: "/somewhere/bin",
            isExecutable: { _ in true })
        #expect(path == "/home-x/.local/bin/claude")
    }

    @Test("falls through to Homebrew, then /usr/local, then PATH")
    func resolutionOrder() {
        let brew = ChildProcessSpec.resolveClaudeExecutable(
            home: "/home-x", pathVariable: "/somewhere/bin",
            isExecutable: { $0 != "/home-x/.local/bin/claude" })
        #expect(brew == "/opt/homebrew/bin/claude")

        let local = ChildProcessSpec.resolveClaudeExecutable(
            home: "/home-x", pathVariable: "/somewhere/bin",
            isExecutable: { $0 == "/usr/local/bin/claude" || $0 == "/somewhere/bin/claude" })
        #expect(local == "/usr/local/bin/claude")

        let fromPath = ChildProcessSpec.resolveClaudeExecutable(
            home: "/home-x", pathVariable: "/a/bin:/somewhere/bin",
            isExecutable: { $0 == "/somewhere/bin/claude" })
        #expect(fromPath == "/somewhere/bin/claude")
    }

    @Test("with nothing installed, the error names the native path")
    func fallsBackToNativePath() {
        let path = ChildProcessSpec.resolveClaudeExecutable(
            home: "/home-x", pathVariable: nil, isExecutable: { _ in false })
        #expect(path == "/home-x/.local/bin/claude")
    }

    @Test("an empty or absent PATH does not crash the resolver")
    func emptyPath() {
        for variable in ["", ":", "::"] {
            let path = ChildProcessSpec.resolveClaudeExecutable(
                home: "/home-x", pathVariable: variable, isExecutable: { _ in false })
            #expect(path == "/home-x/.local/bin/claude")
        }
    }

    /// docs/mac-port.md §2: the login's Keychain service name is `sha256(NFC(configDir))`, so
    /// two spellings of one directory are two different accounts as far as Claude Code is
    /// concerned -- and the second one reads as logged out.
    @Test("config directory spellings collapse to one canonical form")
    func canonicalConfigDirectory() {
        let base = NSTemporaryDirectory() + "cqc-canon-\(UUID().uuidString)"
        try? FileManager.default.createDirectory(atPath: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(atPath: base) }

        let canonical = ChildProcessSpec.canonicalConfigDirectory(base)
        #expect(ChildProcessSpec.canonicalConfigDirectory(base + "/") == canonical)
        #expect(ChildProcessSpec.canonicalConfigDirectory(base + "//") == canonical)
        #expect(ChildProcessSpec.canonicalConfigDirectory(base + "/./") == canonical)
        #expect(ChildProcessSpec.canonicalConfigDirectory(base + "/sub/..") == canonical)
        #expect(canonical.hasSuffix("/") == false)
    }

    @Test("a symlinked spelling resolves to the same canonical path")
    func canonicalResolvesSymlinks() throws {
        let root = NSTemporaryDirectory() + "cqc-link-\(UUID().uuidString)"
        let real = root + "/real", link = root + "/link"
        try FileManager.default.createDirectory(atPath: real, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(atPath: root) }
        try FileManager.default.createSymbolicLink(atPath: link, withDestinationPath: real)

        #expect(ChildProcessSpec.canonicalConfigDirectory(link)
                == ChildProcessSpec.canonicalConfigDirectory(real))
    }

    @Test("the canonical form is NFC, matching Claude Code's own normalisation")
    func canonicalIsNFC() {
        // "é" as e + combining acute (NFD) must normalise to the single NFC codepoint.
        let decomposed = "/tmp/caf\u{0065}\u{0301}-cqc"
        let canonical = ChildProcessSpec.canonicalConfigDirectory(decomposed)
        #expect(canonical.contains("\u{00E9}"))
        #expect(canonical.contains("\u{0301}") == false)
    }

    @Test("the default account leaves CLAUDE_CONFIG_DIR untouched")
    func defaultAccountInheritsEnvironment() {
        let spec = ChildProcessSpec.forAccount(slot: "default", configDirectory: nil)
        #expect(spec.environmentOverrides.isEmpty)
        #expect(spec.arguments == ChildProcessSpec.protocolArguments)
    }

    @Test("a second account pins CLAUDE_CONFIG_DIR to the canonical path")
    func secondAccountPinsConfigDir() {
        let spec = ChildProcessSpec.forAccount(slot: "1", configDirectory: "/tmp/acct-1/")
        #expect(spec.environmentOverrides["CLAUDE_CONFIG_DIR"] == "/tmp/acct-1")
    }

    @Test("two accounts never share one claude session directory")
    func separateWorkingDirectories() {
        let a = ChildProcessSpec.forAccount(slot: "default", configDirectory: nil)
        let b = ChildProcessSpec.forAccount(slot: "1", configDirectory: "/tmp/acct-1")
        #expect(a.workingDirectory != b.workingDirectory)
        // Spawning is a real Claude Code session and fires SessionStart hooks, so it must never
        // run inside a user repo.
        #expect(a.workingDirectory.contains("ClaudeStatusBar"))
    }
}

@Suite("LineFramer")
struct LineFramerTests {

    @Test("splits on newlines and ignores blank lines")
    func splitsLines() {
        let framer = LineFramer()
        #expect(framer.append(Data("a\nb\n".utf8)) == ["a", "b"])
        #expect(framer.append(Data("\n\n".utf8)) == [])
    }

    @Test("reassembles a line split across reads")
    func reassemblesPartialLines() {
        let framer = LineFramer()
        #expect(framer.append(Data("{\"ty".utf8)) == [])
        #expect(framer.append(Data("pe\":1}".utf8)) == [])
        #expect(framer.append(Data("\n".utf8)) == ["{\"type\":1}"])
    }

    @Test("strips a trailing carriage return")
    func stripsCarriageReturn() {
        let framer = LineFramer()
        #expect(framer.append(Data("hello\r\n".utf8)) == ["hello"])
    }

    /// An over-long line is dropped whole, never truncated and parsed: truncated JSON either
    /// fails to parse or, worse, parses into something that looks valid.
    @Test("an oversized line is dropped and counted, and the stream recovers")
    func oversizedLineIsDropped() {
        let framer = LineFramer(maximumLineBytes: 64)
        #expect(framer.append(Data(String(repeating: "x", count: 500).utf8)) == [])
        #expect(framer.overflowCount == 1)
        // the tail of the dropped line, then a good one
        #expect(framer.append(Data("trailing\n".utf8)) == [])
        #expect(framer.append(Data("recovered\n".utf8)) == ["recovered"])
    }
}

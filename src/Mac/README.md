# macOS app

The plan is in [`docs/mac-port.md`](../../docs/mac-port.md). Read its
**"Phase 1 — evidence measured on macOS"** section before changing anything here: it is what this
code is built against, and three of its findings changed the design.

## Status

- **Phase 1 (verify the ground truth on macOS) — done, 2026-09-18.**
- **M0 (protocol) — done, 2026-09-18**, adversarially reviewed and hardened 2026-09-19
  (`docs/reviews/2026-09-19-codex-m0-containment.md`).
- **M1 (menu-bar icon) — done, 2026-09-19.**
- **M2 (the model) — done, 2026-09-19.** The Swift suite replays the repo's own
  `tests/fixtures/*.csv` and reaches the same verdicts as the C# suite.
- **M3 (the panel) — done, 2026-09-19.** `NSPopover` with the panel-v2 layout.
- **M4 (multiple accounts) — done, 2026-09-19** for the per-account path; the `binding` display
  mode is specified but not implemented.
- **M5 (packaging) — done, 2026-09-19.** `scripts/build-app.sh`.

## Layout

```
Sources/ClaudeQuotaCore/    the portable half: protocol client now, the forecast model from M2
Sources/ClaudeStatusBarUI/  AppKit lives here and nowhere else — the gauge, palette, backdrop
Sources/quotaprobe/         M0's command-line probe
Sources/contactsheet/       M1's acceptance gate; exits non-zero if the icon fails the contrast bar
Tests/                      swift-testing; from M2 these replay tests/fixtures/*.csv and must
                            reach the same verdicts as the C# suite
```

## Build and run

```sh
swift build
swift test
swift run quotaprobe                    # one poll against the default login
swift run quotaprobe --poll 20 15       # 20 polls, 15 s apart — shows the cached/live regimes
swift run quotaprobe --config-dir DIR   # poll a second account

swift run contactsheet sheet.png        # icon contact sheet + measured contrast table
swift run contactsheet --menubar        # live status items, one per state, to eyeball

swift run statusbar                     # the real thing: live quota, one icon per account
swift run statusbar --interval 60       # force a fixed poll cadence to watch it work

swift run panelpreview sheet.png        # every panel state rendered offscreen, to judge layout

../../scripts/build-app.sh --install    # build ClaudeStatusBar.app and run it from ~/Applications
../../scripts/build-app.sh --login-item # …and start it at login
../../scripts/build-app.sh --uninstall  # remove both
```

`quotaprobe` spawns a real Claude Code session in a dedicated agent directory under
`~/Library/Application Support/ClaudeStatusBar/`, never in a user repo — spawning fires
SessionStart hooks. Control requests are unmetered, so polling does not consume quota.

## The fixtures are shared on purpose

`Tests/.../Fixtures.swift` locates `tests/fixtures/` **in the repository**, via `#filePath` — it
does not copy them in as SwiftPM resources. docs/mac-port.md: *"What stops the two
implementations drifting is not shared code — it is shared evidence."* A copy can drift from the
file the C# suite reads; the whole acceptance criterion rests on both suites replaying the same
bytes. A test also pins the parsed row counts, so a CSV-reader disagreement surfaces as a failure
instead of as two suites quietly grading different data.

## Two things that are easy to get wrong here

- **`CLAUDE_CONFIG_DIR` must always be the same canonical spelling.** Claude Code derives the
  login's Keychain service name from `sha256(NFC(configDir))`, so a trailing slash or a symlinked
  spelling is a *different account* as far as it is concerned, and reads as logged out.
  `ChildProcessSpec.canonicalConfigDirectory` is the only place that decides this.
- **The icon's signal is the ring's filled fraction, not its hue.** There is no fixed backdrop on
  macOS: measured against the shipped default wallpaper the palette drops to 1.02:1, i.e.
  invisible. What the icon promises is `GaugeRenderer.minimumEdgeContrast` on its *edge*, via a
  keyline in the menu bar's own label colour. Anything that changes the drawing must keep
  `swift run contactsheet` exiting zero.
- **Containment is the stdin pipe first, signals second.** Closing stdin is what covers a force
  quit, where no cleanup code runs at all. `setsid` + `kill(-pgid)` covers the clean exit, and the
  launch sweep covers the one hole: a hung child that never reads the EOF.

The Windows app in `../Windows` keeps building on Windows. What the two share is `docs/` (the spec)
and `tests/fixtures/` (real recorded observations that both test suites replay).

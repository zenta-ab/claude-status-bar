# macOS port — plan

A menu-bar version of the app for macOS, in this repo. Written as a handoff: a Claude Code session
on the Mac should be able to work through it from top to bottom.

## Decision: native Swift, shared spec and fixtures

The Windows app is ~3 900 lines of portable logic (model, forecast, accounts, protocol parsing) and
~3 300 lines of Windows UI (WinForms, GDI+, 18 Win32 calls), plus ~5 900 lines of tests that are
almost entirely portable.

The Mac app is written in **Swift/SwiftUI with AppKit** for the menu bar, and the model is ported
rather than shared. .NET with Avalonia would reuse more code, but the macOS menu bar has conventions
(template images, `NSPopover`, vibrancy) that Avalonia serves poorly, and this project has spent most
of its effort on exactly that kind of pixel-level fit.

**What stops the two implementations drifting is not shared code — it is shared evidence:**

- `docs/*.md` is the single source of truth for protocol, forecast, states and copy. Both apps
  implement the same spec; a change there is a change to both.
- `tests/fixtures/*.csv` are real recorded observations (a real rollover, real replica skew). The
  Swift test suite replays the **same files** and must reach the same verdicts as the C# suite.

## Phase 0 — repo restructure (done on Windows, before any Mac work)

```
docs/              shared spec, including this file
src/Core/          (optional, later) portable .NET logic if the Windows app is ever split
src/Windows/       today's src/ClaudeStatusBar
src/Mac/           the Swift app
tests/             C# tests; fixtures move to tests/fixtures/ and are shared
scripts/           install.ps1, add-account.ps1, check-privacy.ps1 (Windows) + mac equivalents
```

The Windows build, tests, scripts and `ClaudeStatusBar.slnx` must stay green through the move.

## Phase 1 — verify the ground truth on macOS FIRST

Everything below assumes the protocol behaves as it does on Windows. Confirm before writing app code;
if any of this differs, the design changes.

1. `which claude` — where the binary actually lives (`~/.local/bin/claude`, Homebrew, npm shim).
   The Windows app resolves the native install first, then `PATH`; mirror that.
2. Is the config at `~/.claude.json` with the state directory `~/.claude`? Does `CLAUDE_CONFIG_DIR`
   relocate both, as on Windows? (This is what multi-account depends on.)
3. Run the exact protocol by hand:
   `claude -p --input-format stream-json --output-format stream-json --verbose`, then write
   `{"type":"control_request","request_id":"1","request":{"subtype":"get_usage","skip_behaviors":true}}`
   Confirm: `rate_limits.five_hour` with `utilization` and `resets_at`, `subscription_type`, latency
   around 10-20 ms with `skip_behaviors`, and that identical `resets_at` microseconds repeat between
   server refreshes (about every 5 min).
4. `oauthAccount` fields in `.claude.json`: `accountUuid`, `organizationUuid`, `organizationName`,
   `subscription_type` — the multi-account identity key is the account+organisation pair.
5. Menu bar reality: `NSStatusItem` is ~22 pt tall (44 px @2x). Decide whether a colour gauge is
   acceptable or whether the icon must be a template (monochrome) image that the system tints.
   Check it against a light menu bar, a dark one, and "Reduce transparency".
6. Process containment: macOS has no job objects. Confirm the child dies with the app —
   `setpgid` + `kill(-pgid)` on exit, plus a watchdog, and verify no orphan `claude` survives a
   force quit.

Write the answers into this file as you go; they are the Mac equivalent of the evidence section in
`docs/forecast-and-states.md`.

## Phase 2 — milestones

Each one ends with something demonstrable, like the Windows milestones did.

- **M0 — protocol**: a Swift command-line target that spawns the child, sends `get_usage`, prints
  utilization and reset times. Proves Phase 1 in code.
- **M1 — menu bar icon**: `NSStatusItem` with a custom-drawn gauge (outer ring = weekly, gap, inner
  pie = session, forecast slice), drawn with Core Graphics at the right scale. Done when a contact
  sheet of every state at 1x and 2x, on light and dark menu bars, is legible.
- **M2 — the model, ported**: `WindowTracker`, `ForecastMath`, `StateMachine`, `FreshnessOracle`,
  `QuotaModel`. Port the C# tests with them, including the fixture replays
  (`window-shape-*.csv`, `rollover-*.csv`). **The Swift suite must produce the same verdicts as the
  C# suite on the same fixtures** — that is the acceptance criterion for this milestone.
- **M3 — the panel**: an `NSPopover` with the v2 layout from `docs/panel-v2.md` (status box first,
  then Tid/Kvot bars), the refresh control, and the same Swedish copy rules from `Ui/PanelText.cs`.
- **M4 — multiple accounts**: `accounts.json` in `~/Library/Application Support/ClaudeStatusBar/`,
  one child per login, one status item per account or a single binding one, per `docs/multi-account.md`.
- **M5 — packaging**: login item via `SMAppService`, ad-hoc signing for local use, and a `make`/script
  equivalent of `install.ps1`. Note that an unsigned app distributed to others triggers Gatekeeper;
  decide then whether notarisation is worth it.

## Rules that carry over

- **Never show a confident wrong number.** The freshness ladder (Live / Stale / Unknown) and the
  refusal rules exist because `get_usage` cannot report its own staleness.
- **Labels come from the user's own Anthropic data**, never hardcoded.
- **No personal data in the repo.** `scripts/check-privacy.ps1` covers the Windows side; the Mac side
  needs the same check in the pre-commit hook (a small shell version, or call the existing rules).
- The equivalent of the GDI handle rule on macOS is simply that `NSImage` redraws are cheap — but
  still verify memory over days, as the app runs for weeks.

## Open questions for the owner

- Should the Mac app ship the same Swedish UI text, or is this the moment to make the copy
  localisable (Swedish + English)? The Windows app is Swedish-only today.
- Is a shared `src/Core` .NET library worth extracting later, if a third platform ever appears? For
  two platforms, the spec-and-fixtures approach is lighter.

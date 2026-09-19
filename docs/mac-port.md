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

**Status: done, 2026-09-18 — see "Phase 1 — evidence measured on macOS" below.** Items 1, 2, 4 and 6
behave as this plan assumed. Items 3 and 5 differ from Windows in ways that change the design
(latency, the weekly-node heuristic, the fingerprint's weight, and the menu-bar backdrop); the
proposed changes are listed there, not applied silently.

## Phase 1 — evidence measured on macOS (2026-09-18)

Measured on macOS 26.6.2 (arm64), Claude Code **2.1.271**, Xcode 26.6 / Swift 6.3.3, on a 1x
1920x1080 display. Every number below is measured on this machine unless it says otherwise.
The Windows evidence in `docs/forecast-and-states.md` was recorded 2026-09-11 on an earlier CLI
version, so where the two differ it is worth re-checking Windows before assuming the cause is the
platform.

**Verdict: the port's foundations hold.** Binary layout, config layout, `CLAUDE_CONFIG_DIR`
isolation, `oauthAccount`, the window shape and the process model all behave as the design assumes.
Three things differ enough to change the design: **poll latency**, **the menu-bar backdrop**, and
**how often the fingerprint is novel**. Each is called out as **DIFFERS** below.

### 1. Where the binary lives

```
which claude        ~/.local/bin/claude
                    -> symlink to ~/.local/share/claude/versions/2.1.271  (Mach-O arm64)
```

Same native-installer layout as Windows, minus the `.exe`. Not present: `/opt/homebrew/bin/claude`,
`/usr/local/bin/claude`, `/usr/bin/claude`, and no global npm `claude` package.

- **`~/.local/bin/claude` is a symlink into a version-numbered directory.** Auto-update repoints
  the symlink, so the Windows rule "re-resolve the path on every launch attempt"
  (`ChildProcessSpec.ResolveExePath` is a `Func`, Codex review High #1) matters here for the same
  reason and must be carried over. Resolve the symlink at launch, never cache the target.
- Proposed resolution order for the Mac port, mirroring `ChildProcessSpec.ResolveClaudeExe`:
  `~/.local/bin/claude` → `/opt/homebrew/bin/claude` → `/usr/local/bin/claude` → first `claude` on
  `PATH`. Unlike Windows there is no shim problem to screen out: there is no `.cmd` equivalent, and
  a Homebrew or npm `claude` is an ordinary executable or symlink that can be supervised normally.
- A GUI app launched from Finder or as a login item does **not** inherit the shell's `PATH`, so the
  `PATH` fallback will usually be empty in production. The explicit paths above are the real
  mechanism; `PATH` is only a backstop for an unusual install.

### 2. Config and state directories — as designed

| | Path |
|---|---|
| Default account config | `~/.claude.json` |
| Default account state | `~/.claude/` |
| Second account (`CLAUDE_CONFIG_DIR=<dir>`) | `<dir>/.claude.json`, `<dir>/sessions`, `<dir>/backups` |

Confirmed identical to Windows, including the asymmetry `docs/multi-account.md` already documents:
for the **default** account `.claude.json` is a *sibling* of `~/.claude`, not inside it
(`~/.claude/.claude.json` does not exist), while under `CLAUDE_CONFIG_DIR` the whole state directory
relocates and the literal `<configDir>/.claude.json` composition is correct. The
`AccountsConfig`/`ChildProcessSpec` logic ports unchanged.

**`CLAUDE_CONFIG_DIR` isolates the login.** A child started with an empty config dir returned:

```json
{"rate_limits": null, "rate_limits_available": false, "subscription_type": null}
```

— exactly the Windows result, and exactly what `docs/multi-account.md` rests on. It created
`<dir>/.claude.json` with machine-level keys only (`firstStartTime`, `machineID`, `userID`, …) and
**no** `oauthAccount`. So *N accounts = N config directories = N child processes* holds on macOS.

**DIFFERS (storage, not behaviour): credentials live in the login Keychain, not in the config dir.**
There is no `~/.claude/.credentials.json`; instead the login keychain holds a generic-password item
with service `Claude Code-credentials`. This does **not** break the isolation above — the empty
config dir returned no account even though the keychain item was readable, so the config directory,
not the keychain, is what selects the login.

Two consequences for M4:

- The `add-account` script cannot work by creating a directory and copying anything; it must run an
  interactive `claude` login in the new `CLAUDE_CONFIG_DIR`, which `scripts/add-account.ps1` already
  does for its own reasons (token rotation invalidates copies). The mac script mirrors it.
#### RESOLVED: the keychain item is namespaced per config directory

The first pass left this open. It is now answered from Claude Code's own shipped code (2.1.271),
which builds the keychain service name like this:

```js
var UJ = "-credentials";
function nP(n = "") {
  let e = process.env.CLAUDE_SECURESTORAGE_CONFIG_DIR,
      t = e !== undefined ? !e : !process.env.CLAUDE_CONFIG_DIR,   // "is this the default login?"
      r = e !== undefined ? e.normalize("NFC") : configDir(),
      c = t ? "" : `-${sha256(r).hex.substring(0, 8)}`;            // per-config-dir suffix
  return `Claude Code${OAUTH_FILE_SUFFIX}${n}${c}`;
}
```

- The **default** login (`CLAUDE_CONFIG_DIR` unset) gets the bare service `Claude Code-credentials`.
- **Every other** config directory gets `Claude Code-credentials-<first 8 hex of sha256(configDir)>`.
- The `-a` (account) attribute is *not* what separates them: it is
  `process.env.USER || os.userInfo().username`, falling back to `claude-code-user` — the same value
  for every account. Only the **service** differs. Checking `acct` alone would have given the wrong
  answer.
- The file-based fallback is namespaced the same way: `join(secureStorageDir, ".credentials.json")`,
  where `secureStorageDir` is `CLAUDE_SECURESTORAGE_CONFIG_DIR ?? configDir()`.

Confirmed on this machine as far as it can be without a second login: the default item exists under
the bare name, and the predicted suffixed name for a never-logged-in config directory does not
exist.

**So a second login cannot log the first account out, and `CLAUDE_CONFIG_DIR` alone is sufficient
for M4 — no keychain work is needed.** Two caveats that do change the implementation:

- **The config-dir path must be canonical and stable.** The suffix is a hash of the NFC-normalized
  directory *string*, so `/a/b`, `/a/b/` and a symlinked spelling of the same directory produce
  three different service names. A login performed under one spelling is invisible to a child
  started under another, which would present as a silently logged-out account. The add-account
  script and the app must write and pass the **same canonical absolute path** (resolved symlinks,
  no trailing slash, NFC).
- `security add-generic-password` is called with `-U` (update in place). Two config dirs that
  resolved to the same service name would therefore overwrite each other silently. They cannot,
  except via `CLAUDE_SECURESTORAGE_CONFIG_DIR=""`, which forces the default name — the app must
  never set that variable.

Still worth one cheap confirmation when a second account first exists (not a blocker): after the
login, check that both the bare and the suffixed service names are present.

### 3. The protocol — same shape, different latency

`claude -p --input-format stream-json --output-format stream-json --verbose`, then

```json
{"type":"control_request","request_id":"1","request":{"subtype":"get_usage","skip_behaviors":true}}
```

answers on stdout with

```json
{"type":"control_response","response":{"request_id":"1","subtype":"success","response":{
  "rate_limits":{"five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
                 "seven_day":{"utilization":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"}, …},
  "rate_limits_available":true, "subscription_type":"team", "session":{…}}}}
```

`rate_limits.five_hour` with `utilization` and `resets_at` ✔, `seven_day` ✔, `subscription_type` ✔.
`skip_behaviors: true` is honoured (`"behaviors": null`). The envelope nesting is
`root.response.request_id` and `root.response.response.rate_limits`, which is what
`EnvelopeParser` and `UsageParser`'s depth-first search already expect — both port unchanged.
The child exits cleanly (`exit=0`) when stdin is closed.

#### DIFFERS: latency is ~10x the Windows figure, and bimodal

56 polls, 15 s apart, over 14 min on one long-lived child:

| | n | min | median | max |
|---|---|---|---|---|
| all polls | 56 | 16 ms | 209 ms | 1164 ms |
| cached replies (< 150 ms) | 27 | 16 ms | — | 146 ms |
| live replies (>= 150 ms) | 29 | 156 ms | — | 1164 ms |

The plan's "around 10-20 ms with `skip_behaviors`" holds only for the *cached* regime, and even
there the floor is ~16-30 ms with excursions to 146 ms. When the CLI actually goes to the server the
reply takes 230-570 ms, and the first reply after spawn took 1164 ms.

Impact: none on correctness — the poll already runs off the UI path — but the Mac port must **not**
call `get_usage` synchronously from anything that blocks drawing, and the child-request timeout must
be sized for the ~1.2 s cold case, not for 20 ms. Worth re-measuring on Windows on 2.1.271: this is
more likely a CLI/server change since 2026-09-11 than a platform difference.

#### Window shape: confirmed, exactly as on Windows

All 56 replies carried `five_hour.resets_at` = `2026-09-18T16:10:00` **to the second**, while session
usage rose 68 % → 78 % over 14 min. A rolling window would have slid 14 min. **Fixed tumbling
window, confirmed independently on macOS.** All jitter is sub-second, far inside the ±120 s
window-key rule.

#### DIFFERS (in degree): identical microseconds repeat, but novel ones are common

The design's premise — "between refreshes it returns byte-identical `resets_at` strings; the
microsecond digits are the snapshot fingerprint" — is confirmed, but it describes only half the
traffic here:

- 26 distinct fingerprints over 56 polls.
- Two fingerprints repeated, each for a ~5 min hold: `…:00.431931` x21 and `…:00.650307` x11 —
  the ~5 min server refresh, exactly as documented.
- Between those holds, runs of 8-16 consecutive polls each returned a **novel** microsecond value
  (~250 ms latency), with usage wobbling 68/69/70 and 76/77/78 — replica skew visible per reply.
- `…:00.650307` was served at 13:31:09, replaced by 16 novel-fingerprint replies, then **came back**
  at 13:35:09 with usage regressing 78 % → 76 %. A previously-seen fingerprint returning after
  newer data — the exact replica-revert the dedup rule exists for.

Two session-% regressions were observed. The dedup rule caught one (78 → 76, previously-seen
fingerprint) and **not** the other (70 → 68 at 13:25:54, novel fingerprint). The monotone envelope
`P = max(P, sample)` is what caught the second, and is therefore load-bearing on macOS in a way the
Windows evidence understates.

Nothing in the model breaks, but two statements in `docs/forecast-and-states.md` should be softened
rather than left as-is, since a Swift port written from them would over-trust the fingerprint:

- Ingest rule 2 ("a sample whose fingerprint was already seen in this window is skipped") is correct
  but catches materially less traffic than the Windows log suggests. It is a *dedup* optimisation,
  not the primary defence against replica skew; the monotone envelope is.
- The Stale rule "session fingerprint unchanged for more than 20 min" is unaffected: the longest
  observed unchanged run was ~5 min.

#### DIFFERS (latent bug, not yet firing): the weekly-node heuristic is one key-reorder from wrong

`rate_limits` now carries many sibling keys, in this wire order:

```
 0 five_hour        1 seven_day        2 seven_day_oauth_apps   3 seven_day_opus
 4 seven_day_sonnet 5 cinder_cove      6 extra_usage            7 limits
 8 seven_day_cowork 9 seven_day_omelette … 22 seven_day_breakdown  23 model_scoped
```

`UsageParser.FindWeeklyAllModelsNode` takes the **first** `seven_day`-ish key with no
`opus`/`sonnet`/`haiku` qualifier. Today that is `seven_day` and the answer is right. But
`seven_day_oauth_apps`, `seven_day_cowork`, `seven_day_omelette` and `seven_day_breakdown` carry no
model qualifier either — all null right now, so the null check is not what saves it; wire order is.
If the server ever emits one of them before `seven_day`, or populates it, the weekly ring silently
shows the wrong bucket. That is precisely a "confident wrong number".

**Proposed fix, for both implementations:** prefer the exact key `seven_day` (and `five_hour`), and
fall back to the substring search only when the exact key is absent. Better still, the response now
carries a self-describing array:

```json
"limits":[{"group":"session","kind":"session","percent":76,"resets_at":"…650307+00:00"},
          {"group":"weekly","kind":"weekly_all","percent":41,"resets_at":"…650340+00:00"},
          {"group":"weekly","kind":"weekly_scoped","percent":0,"scope":{"model":{…}}}]
```

`kind: "session"` and `kind: "weekly_all"` match `five_hour` and `seven_day` bit-for-bit, including
the microseconds. Reading `limits[]` by `kind` removes the guesswork entirely. Recommend: read
`limits[]` when present, fall back to the exact keys, fall back to the substring search last.

### 4. `oauthAccount` — every field the design needs is present

Present in `~/.claude.json` (names only; values are this machine's real account):

```
accountUuid  organizationUuid  organizationName  organizationType  organizationRole
seatTier  billingType  emailAddress  displayName  fullName  workspaceRole
hasExtraUsageEnabled  accountCreatedAt  subscriptionCreatedAt  profileFetchedAt
claudeCodeTrialEndsAt  claudeCodeTrialDurationDays  ccOnboardingFlags
organizationRateLimitTier  userRateLimitTier
```

The identity key `AccountIdentity.StateKey` = `<accountUuid>_<organizationUuid>` is constructible
unchanged, and `get_usage` returned `subscription_type: "team"` for this login, so the label ladder
in `docs/multi-account.md` ports as written. This account's `organizationName` is a real team name,
not the auto-generated `<email>'s Organization` form, so step 2 of the ladder applies — the
skip-the-auto-name rule was not exercised here and stays unverified on macOS.

### 5. The menu bar — colour survives, but only with a keyline

Measured by running a real `NSStatusItem` and by rendering the ported glyph geometry
(`GaugeRenderer.RingGeometry`: `band = s*0.155`, `rOuter = s*0.5 − band*0.5 − s*0.02`) offscreen
with Core Graphics.

```
NSStatusBar.system.thickness    = 22.0 pt          (as the plan assumed)
button.frame                    = (0, 0, 24, 22)   with item.length = 24
NSApp.effectiveAppearance       = NSAppearanceNameDarkAqua
button.effectiveAppearance      = NSAppearanceNameVibrantDark
labelColor @ VibrantDark        = #FFFFFF, alpha 0.90
labelColor @ VibrantLight       = #000000, alpha 0.70
```

- **The status-item button reports the *menu bar's* own appearance** (`VibrantDark`/`VibrantLight`),
  separately from the app's. That is the macOS replacement for the Windows `taskbarDark` flag, and
  it is read from `item.button!.effectiveAppearance`, not `NSApp.effectiveAppearance`.
- A non-template (colour) `NSImage` is accepted; `isTemplate` is honoured either way.

#### DIFFERS (this one forces a design change): there is no `#202020`

The Windows icon contract is "≥ 5:1 on `#202020`" because the taskbar is a known fixed colour. The
macOS menu bar is translucent over the desktop picture, so the backdrop is **arbitrary**. Contrast
of the existing palette against backdrops the menu bar can actually sit over:

| state | black | #1E1E1E | #808080 | #F0F0F0 | white | blue wall | orange wall | green wall |
|---|---|---|---|---|---|---|---|---|
| Safe `#12A277` | 6.46 | 5.13 | 1.21 | 2.85 | 3.25 | 3.47 | **1.12** | 1.58 |
| Tight `#E8A020` | 9.48 | 7.53 | 1.78 | 1.94 | 2.22 | 5.09 | 1.64 | 2.31 |
| DryEarly `#E23D28` | 4.93 | 3.92 | **1.08** | 3.74 | 4.26 | 2.65 | 1.17 | **1.20** |
| Spent (dim 0.85) | 3.76 | 3.15 | 1.12 | 3.19 | 3.54 | 2.18 | 1.16 | **1.06** |
| Measuring `#5C6672` | 3.60 | 2.86 | 1.48 | 5.12 | 5.84 | 1.93 | 1.61 | **1.14** |

The palette clears 5:1 on a dark backdrop and **collapses to 1.06-1.78:1** on a mid-grey or
saturated wallpaper. On an orange desktop the Safe glyph is invisible. Ported as-is, the icon would
be unreadable for any user with a light or colourful wallpaper — the majority.

Three variants were rendered at 22 px and 44 px over all eight backdrops:

- **A — flat colour (the Windows design as-is).** Fails as above; the glyph's outline disappears
  into mid-grey, orange and green.
- **B — colour + keyline.** Same hues, plus a translucent black halo under the ring and a 1 pt
  keyline in the menu bar's own `labelColor` (white on a dark bar, black on a light one). The
  *edge* then measures **3.63:1 worst case** (orange wallpaper), 3.95 on mid-grey and 5.13-21.0 on
  the rest — clearing the 3:1 minimum for non-text UI on every backdrop tested, while the hue
  still carries severity wherever the backdrop allows.
- **C — template (monochrome), system-tinted.** Always legible by construction, but **all five
  states look nearly identical**: severity stops being readable at a glance, which is the icon's
  entire job.

**Recommendation: variant B.** Keep the colour gauge, add the appearance-matched keyline + halo, and
treat the *filled fraction of the ring* — not the hue — as the primary signal, so the icon still
works for a user who cannot separate the hues from their wallpaper. Concretely this replaces the
Windows icon contract "≥ 5:1 against `#202020`" with **"the glyph edge measures ≥ 3:1 against every
backdrop in the test set, and the keyline colour is taken from `button.effectiveAppearance`"**, and
`IconContrastTests` ports to that assertion instead of the single-background one.

#### The real backdrop, measured without Screen Recording

`screencapture` fails here (`could not create image from display`) for want of Screen Recording
permission, but the backdrop can be measured two other ways that need no permission at all, and both
work:

1. **The user's actual wallpaper.** `NSWorkspace.desktopImageURL(for:)` gives the file; decoding it
   and sampling the top 22 pt in 22 pt-wide columns gives the real per-icon-width backdrop. On this
   machine (stock `DefaultDesktop.heic`, 3840x2160, a blue sky) the strip's mean is sRGB
   (0.158, 0.454, 0.695) and its per-column relative luminance runs **0.095 to 0.202**.
2. **The opaque material.** `NSVisualEffectView(material:)` rendered offscreen via
   `cacheDisplay(in:to:)` returns the flat colour the system falls back to — which is exactly the
   "Reduce transparency" menu bar: `#444444` dark / `#D6D6D6` light for `.menu`.

Contrast against those **measured** backdrops, not synthetic ones:

| state | real wallpaper, darkest column | real wallpaper, brightest column | Reduce Transp. dark `#444444` | Reduce Transp. light `#D6D6D6` |
|---|---|---|---|---|
| Safe `#12A277` | 2.23 | 1.28 | 3.00 | 2.24 |
| Tight `#E8A020` | 3.27 | 1.88 | 4.40 | 1.52 |
| DryEarly `#E23D28` | 1.70 | **1.02** | 2.29 | 2.93 |
| Spent `#9AA3AE` | 2.84 | 1.63 | 3.81 | 1.76 |
| Measuring `#5C6672` | 1.24 | 1.40 | 1.67 | 4.02 |
| keyline `#FFFFFF` | 7.24 | 4.17 | 9.74 | **1.45** |

This is the **shipped default macOS wallpaper**, not a contrived worst case, and the DryEarly red
measures 1.02:1 against it — invisible. It confirms the flat-colour icon cannot ship, and it
confirms the keyline must be **appearance-derived**: a fixed white keyline collapses to 1.45:1 on a
light menu bar, while black on that same bar measures 14.5:1. Reading it from
`button.effectiveAppearance` (§5 above) is what makes it work in both.

**Still needs the owner, but no longer blocking:** a photograph or screenshot of the finished icon
in a real menu bar is the only thing the above cannot replace, because it is the one check that
covers the window server's actual blur and the physical size on a Retina panel. Options, cheapest
first: (a) run the M1 contact-sheet tool, which can read the wallpaper and render the glyph over the
real measured backdrop unattended; (b) the owner grants Screen Recording once and re-runs it with
`screencapture`; (c) the owner takes a manual screenshot. (a) is enough to gate M1; (b) or (c) is
worth doing once before shipping. The display here is 1x, so 2x is verified only at the rendering
level (44 px glyphs inspected), never on real Retina hardware.

`NSWorkspace.accessibilityDisplayShouldReduceTransparency` and `…ShouldIncreaseContrast` are
readable at runtime (both `false` here) and should select the opaque-material numbers above.

### 6. Process containment — the child dies with the app

macOS has no job objects. Measured behaviour of `claude` spawned with its own session/process group
(`setsid`) and stdin on a pipe:

| Test | Result |
|---|---|
| Parent `SIGKILL`ed (force quit) | child **gone within 2 s**, and its `node` descendant with it |
| `SIGTERM` to the child's process group | child and descendant gone **immediately**; parent unaffected |
| `SIGKILL` to the child's process group | same, immediately |
| stdin closed (normal quit) | child exits cleanly, `exit=0` |
| child `SIGSTOP`ped, then parent `SIGKILL`ed | child **survives**, reparented to `launchd` (ppid 1, state `TNs`) |

- The primary containment mechanism is **the stdin pipe**, not signals: when the app dies the write
  end closes, `claude` reads EOF and exits. This covers force quit, which `kill(-pgid)` on a normal
  exit path cannot.
- `claude` spawns one `node` child. It is in the same process group and dies with it in every test
  above — no orphan survived a normal or forced shutdown.
- Putting the child in **its own** session via `setsid` is required, not optional: it is what makes
  `kill(-pgid)` safe. Killing the app's own group would kill the app.
- **The one hole:** a *hung* child (here simulated with `SIGSTOP`) survives a force quit as an
  orphan on `launchd`, because a stopped process never reads the EOF. So the watchdog the plan
  already calls for is genuinely needed, not belt-and-braces. Proposed: on launch, before spawning,
  sweep for `claude` processes whose parent is 1 and whose working directory is this app's agent
  directory, and `SIGKILL` them. The per-account agent working directory
  (`ChildProcessSpec.ForAccount`) is what makes that sweep safe — it can never match a `claude` the
  user started themselves.

An earlier reading of this test reported the child as surviving `SIGTERM`; that was a misread —
`ps` still lists an unreaped **zombie** (`stat Z`). The child had already exited. Corrected above.

### 7. Repo hygiene — `check-privacy` does not run on macOS

Two gaps to close as part of the Mac work, both already implied by the plan:

- `.githooks/pre-commit` execs `powershell`, which is not installed on this machine, so the hook
  silently fails to protect a commit made from macOS.
- `check-privacy.ps1`'s `user path` pattern only matches a Windows `<drive>:\Users\<name>`. The macOS equivalent
  `/Users/<name>` is not covered, and the allow-list `\Users\(someone|user|…)` has no POSIX form.
  The mac check needs `(?<!example)/Users/[A-Za-z0-9._-]+` with `/Users/someone` allowed, plus the
  existing email/UUID/token patterns.

This evidence section deliberately contains no email address, organisation name, account UUID or
real user path; every path is written relative to `~`.

### Summary of proposed design changes

| # | Change | Why |
|---|---|---|
| 1 | Request timeout sized for ~1.2 s, not 20 ms; poll never on the drawing path | latency is 16-1164 ms, bimodal |
| 2 | Read `rate_limits.limits[]` by `kind`; exact-key fallback; substring search last | `seven_day_*` siblings can silently win the heuristic |
| 3 | Soften the fingerprint's role in the spec; the monotone envelope is the primary skew defence | only 2 of 26 fingerprints repeated; one regression had a novel fingerprint |
| 4 | Icon contract becomes "glyph edge ≥ 3:1 on every backdrop", via a keyline from `button.effectiveAppearance` | no fixed backdrop colour exists on macOS |
| 5 | Containment = stdin pipe (primary) + `setsid` + `kill(-pgid)` (clean exit) + orphan sweep at launch | force quit is covered by EOF; a hung child is not |
| 5b | `CLAUDE_CONFIG_DIR` must always be the same canonical absolute path (symlinks resolved, no trailing slash, NFC) | the keychain service name is a hash of that string; a different spelling reads as logged out |
| 6 | macOS privacy check + a `sh` pre-commit hook | the PowerShell hook cannot run here |

### Still unverified

- ~~That a real second login actually creates the predicted suffixed keychain item.~~
  **Confirmed 2026-09-19**: a second account was logged in to its own `CLAUDE_CONFIG_DIR` and both
  logins now answer independently — the Team account and a personal Max account, each with its own
  child process and its own menu-bar icon. The first login was unaffected, exactly as §2 predicted
  from the decompiled selector.
- The icon in a real menu bar, at 2x, on real Retina hardware, and under the window server's actual
  blur (§5 — the backdrop itself is now measured without Screen Recording).
- ~~The `<email>'s Organization` label-skip rule.~~ **Confirmed 2026-09-19** on the real personal
  Max login, whose `organizationName` is exactly that auto-generated form. The ladder skipped it
  and fell through to the plan step, producing "Max" — which is the documented behaviour, exercised
  against real data for the first time.
- Whether the latency and fingerprint differences are macOS-specific or a CLI change since
  2026-09-11 — re-measure on Windows with 2.1.271.

## Working on this repo from macOS — toolchain

Installed 2026-09-19. There is no Homebrew on this machine, so both went in without it.

| Tool | Where | Install |
|---|---|---|
| Swift 6.3.3 / Xcode 26.6 | system | already present |
| .NET SDK 10.0.204 | `~/.dotnet` | `dotnet-install.sh --version 10.0.204`; `DOTNET_ROOT` + `PATH` in `~/.zshrc` |
| Codex CLI 0.155.1 | npm global | `npm install -g @openai/codex`; `~/.local/bin/codex` is a wrapper, see below |

### What .NET can and cannot do here — measured, not assumed

The C# projects target `net10.0-windows` with WinForms, and the test project references the app.

| | on macOS |
|---|---|
| `dotnet build src/Windows/ClaudeStatusBar.csproj -p:EnableWindowsTargeting=true` | **succeeds** |
| `dotnet build tests/ClaudeStatusBar.Tests/... -p:EnableWindowsTargeting=true` | **succeeds** |
| `dotnet test tests/ClaudeStatusBar.Tests/...` | **fails, and cannot be made to work** |
| `dotnet build tests/FakeClaudeChild/...` (`net10.0`) | succeeds, no flag needed |

The run fails because the test host needs the `Microsoft.WindowsDesktop.App` runtime, which does
not exist for `osx-arm64` and is not installable:

```
Testhost process ... exited with error: You must install or update .NET to run this application.
Framework: 'Microsoft.WindowsDesktop.App', version '10.0.0' (arm64)
No frameworks were found.
```

Without `-p:EnableWindowsTargeting=true` even the build stops at
`error NETSDK1100: To build a project targeting Windows on this operating system, set the
EnableWindowsTargeting property to true`. The flag is passed on the command line so no project
file has to change; adding `<EnableWindowsTargeting>true</EnableWindowsTargeting>` to
`Directory.Build.props` would make it automatic and is a no-op on Windows, but has deliberately
**not** been done — it is the Windows build's configuration and nobody has asked for it.

**So from macOS you can catch compile-level drift in the C# code, but not behavioural drift.**
That is a real, accepted gap (owner's decision, 2026-09-19): the C# suite is run on Windows,
manually, when it matters. The two candidate fixes, if it ever stops being acceptable, are a
`windows-latest` CI runner, or extracting the portable model into a `net10.0` project (the
optional `src/Core` in Phase 0) so its tests run anywhere. Until then, **the shared fixtures in
`tests/fixtures/` and the shared spec in `docs/` are the only thing keeping the two
implementations honest** — which is exactly what this document's opening section says, and is now
load-bearing rather than belt-and-braces.

### Codex

Codex is the independent counter-voice for architecture, security, hard bugs and final review of
large changes (`~/.claude/CLAUDE.md`). Set up as:

- `~/.codex/config.toml` — `sandbox_mode = "read-only"`, `approval_policy = "on-request"`.
  Verified to parse with `codex --strict-config doctor`.
- `~/.local/bin/codex` — a wrapper, not a symlink. npm's global prefix under nvm is tied to the
  *active* node version, so a symlink dangles after `nvm use` and fails in a way that hides the
  cause. The wrapper prefers the active node's copy, falls back to any other installed version,
  and otherwise prints the install command.
- `~/.claude/commands/codex/{setup,review,adversarial-review}.md` — the `/codex:*` commands
  `CLAUDE.md` already assumed existed but which had never been created.

Every one of those commands starts by making Codex quote a real line from a real file and
checking it. A sandboxed Codex that cannot read the repo still answers, and the answer looks like
a review while being invented — that check is what stops it being passed on.

## Phase 2 — milestones

Each one ends with something demonstrable, like the Windows milestones did.

- **M0 — protocol**: a Swift command-line target that spawns the child, sends `get_usage`, prints
  utilization and reset times. Proves Phase 1 in code. **Done, 2026-09-18** — `src/Mac`,
  `swift run quotaprobe`. `ClaudeQuotaCore` carries the protocol client (`ChildProcessSpec`,
  `ChildProcess`, `LineFramer`, `ControlChannel`, `UsageParser`); M2 adds the model beside it.
  25 unit tests, and verified live against the real CLI:
  - reads the live account via `limits[]` (not the substring fallback), 879-1123 ms cold, then
    16-75 ms on the cached hold with a byte-identical fingerprint — the Phase 1 measurements,
    reproduced by the port itself;
  - a logged-out config directory reads as *"not logged in"*, never as 0 %;
  - force-quitting the parent (`SIGKILL`, no cleanup possible) took the child **and** its `node`
    descendant with it inside 0.25 s; the child runs in its own process group
    (`POSIX_SPAWN_SETSID`), distinct from the app's;
  - the launch sweep cleared a deliberately hung, orphaned child out of the agent directory, and
    left an orphaned `claude` that the *user* had started from their own repo untouched.

  **Reviewed adversarially on 2026-09-19** (`docs/reviews/2026-09-19-codex-m0-containment.md`):
  verdict *block*, and most of it held. The load-bearing one: pipes were not `FD_CLOEXEC`, so with
  two or more accounts a later child inherits an earlier child's stdin write end and that earlier
  `claude` never sees EOF — the primary containment mechanism, defeated, and invisible to a
  single-child measurement. Also fixed: pid-reuse and prefix-collision races in the sweep, a
  blocking stdin write that outlived its own timeout, unbounded response retention, JSON booleans
  reading as `1.0`, an undrained stderr (a child deadlock), an inherited
  `CLAUDE_SECURESTORAGE_CONFIG_DIR` that would collapse every account onto one login, and
  wall-clock durations in a codebase whose contract is monotonic. 47 tests, re-verified live.
- **M1 — menu bar icon**: `NSStatusItem` with a custom-drawn gauge (outer ring = weekly, gap, inner
  pie = session, forecast slice), drawn with Core Graphics at the right scale.
  **Done, 2026-09-19** — `Sources/ClaudeStatusBarUI`, `swift run contactsheet [out.png]`
  (add `--menubar` to put live status items in the bar).
  - The Windows contract "≥ 5:1 against `#202020`" is replaced by **`GaugeRenderer.minimumEdgeContrast`
    = 3:1 on the glyph's EDGE, against every backdrop**, because macOS has no fixed backdrop. The
    keyline colour comes from the status item button's `effectiveAppearance`, which reports the
    menu bar's own `VibrantDark`/`VibrantLight`.
  - `contactsheet` measures the backdrops instead of assuming them: the user's real wallpaper via
    `NSWorkspace.desktopImageURL`, sampled in 22 pt columns, plus the opaque material that
    "Reduce transparency" produces, plus a hostile synthetic set. It exits non-zero if the gate
    fails, so it can gate a build.
  - Measured on this machine: worst case **3.63:1 on a saturated orange wallpaper**, 3.95 on
    mid-grey, 4.16–21.0 elsewhere. For contrast, the *hue* alone measures 1.02–9.48:1 across the
    same backdrops — which is exactly why the signal is the ring's filled fraction and the edge,
    not the colour.
  - Two bugs the tests caught, both invisible to the eye at a glance: the halo and keyline were
    drawn **outside** the bitmap and clipped (alpha 253 on the outermost pixel row — a flat-sided
    ring), and the contact sheet reported a keyline alpha the renderer no longer used. The
    geometry now accounts for every stroke it draws, and the alpha lives in one constant that both
    the renderer and the sheet read.
  - A third bug, found only by putting real items in a real menu bar: **the button's effective
    appearance is wrong at creation time.** On a system in Dark mode, a status item button queried
    right after `statusItem(withLength:)` reports `VibrantLight`, and only reports `VibrantDark`
    once the window server has placed it — about a second later. Reading it once therefore picks a
    *black* keyline for a *dark* bar, the exact case the keyline exists to prevent.
    `StatusItemController` now observes `effectiveAppearance` and re-renders on every change,
    which also covers the user switching Light/Dark or an Auto schedule firing.
  - Still open, and it needs the owner: the sheet cannot photograph a *real* menu bar (no Screen
    Recording permission), and this display is 1x, so 2x is verified only at the rendering level.
- **M2 — the model, ported**: `WindowTracker`, `ForecastMath`, `StateMachine`, `FreshnessOracle`,
  `QuotaModel`. Port the C# tests with them, including the fixture replays
  (`window-shape-*.csv`, `rollover-*.csv`). **The Swift suite must produce the same verdicts as the
  C# suite on the same fixtures** — that is the acceptance criterion for this milestone.
  **Done, 2026-09-19.** ~1 100 lines ported into `ClaudeQuotaCore`; 105 tests green.
  - The replay tests read `tests/fixtures/*.csv` **in the repository**, located from `#filePath`
    — deliberately not copied in as SwiftPM resources, because a second copy can drift from the
    one the C# suite reads, which is the exact failure this arrangement exists to prevent.
    A test asserts the parsed row counts (127 and 36) so a reader disagreement shows up as a
    disagreement rather than as silently different data.
  - Both replay suites reach the C# verdicts: one genuine rollover separated from the 2026-09-09
    jitter, the 94 % sample kept rather than dropped, the monotone envelope holding per
    observation, and the rollover slice's "old replicas never win back / gate lifts after five
    minutes / cold start reads awaiting-reset" sequence.
  - The spec's worked examples are asserted as exact oracles, not plausibility ranges:
    P = 56, E = 132, t_reset = 168 → r = 0.424 %/min, shortfall = 64 min → DryEarly immediately;
    and 60 % at 0.6 %/min with 4 h left → shortfall 173 min.
  - Cadence and guard regressions are covered too: the endless-1 s-loop fix, the expired-Spent
    fast recovery, backoff not being overridden by a stale deadline, and clock jumps in both
    directions forcing Measuring + Stale with every forecast field nil.
  - `statusbar` now runs on the model: polling follows `QuotaModel.nextPollDelay`, and a separate
    1 Hz tick runs `evaluate`, because the freshness ladder is specified to run on the
    always-running timer precisely so a hung poll cannot leave a confident number frozen on
    screen.
  - **Not yet ported**: the window-shape CSV *writer*, so `warmStart` currently replays an empty
    set. The replay code itself is ported and tested against the fixtures; only the on-disk
    history the app would feed it is missing.
- **M3 — the panel**: an `NSPopover` with the v2 layout from `docs/panel-v2.md` (status box first,
  then Tid/Kvot bars), the refresh control, and the same Swedish copy rules from `Ui/PanelText.cs`.
  **Done, 2026-09-19.**
  - `TimeText` and `PanelText` ported with their exact strings; the C# suite's assertions are
    reproduced verbatim, including the spec's worked example rendering as
    *"⚠ Kvoten tar slut kl 12:44 (om 1 h 44 min)"* over
    *"1 h 4 min före reset kl 13:48, om du fortsätter i samma takt"* — the depletion countdown
    and the shortfall are different numbers and the copy must not confuse them.
  - One cross-implementation trap found and closed: .NET's `ToString("F0")` rounds half **away
    from zero**, C's `%.0f` rounds half to **even**, so every percentage landing exactly on .5
    would have read one point apart between the two apps forever. `TimeText.percent` rounds the
    .NET way, with a test.
  - `NSPopover` with `.transient` behaviour replaces the Windows `DismissWatcher` outright —
    253 lines of Win32 hooking whose whole job (close on click-away) is framework behaviour here.
  - `panelpreview` renders every state offscreen to a PNG, so the layout can be judged without
    clicking through a live menu bar. It caught a real bug no string test would: the panel read
    ages against `Date()` instead of the instant it was rendering for, so a preview reported
    "uppdaterad för 8 d 7 h sedan".

- **M4 — multiple accounts**: **done, 2026-09-19** for the per-account path. `accounts.json`,
  one child + one model + one icon per login, `scripts/add-account.sh`, the label ladder, and the
  panel's other-account rows. Verified live with two real logins (a Team seat and a personal Max
  seat). The `binding` display mode (one icon for the account closest to its limit) is specified
  but not implemented.

  **The identity guard is now confirmed on real data**: both accounts share the same
  `accountUuid` and differ only in `organizationUuid`, so `logs/<accountUuid>_<organizationUuid>/`
  produced two separate history directories. Keying on `accountUuid` alone — the Windows bug —
  would have poured two plans' contradictory percentages into one file.
- **M4 — multiple accounts**: `accounts.json` in `~/Library/Application Support/ClaudeStatusBar/`,
  one child per login, one status item per account or a single binding one, per `docs/multi-account.md`.
- **M5 — packaging**: **done, 2026-09-19** — `scripts/build-app.sh` builds
  `ClaudeStatusBar.app`, ad-hoc signs it, installs it to `~/Applications`, and can register it as
  a login item (`--login-item`) or remove both (`--uninstall`).
  - `LSUIElement` keeps it out of the Dock and the ⌘-Tab switcher.
  - **Not `SMAppService`**, despite the plan naming it: that API requires the app to register
    *itself* from inside its own bundle, which an install script cannot do on its behalf. A
    System Events login item is the scriptable equivalent, and it has the advantage of appearing
    in System Settings → General → Login Items where the user can see and remove it.
  - The bundle is ad-hoc signed, **not notarised**. Fine on the machine that built it; copied to
    another Mac, Gatekeeper refuses it until the user right-clicks → Open. Notarisation needs a
    paid Developer ID and is only worth it if this is ever distributed.

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

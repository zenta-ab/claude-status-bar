# Backlog

## Pattern-aware forecast (user request, 2026-09-11)

**Problem.** Forecasts extrapolate the recent pace uniformly over the clock, but the user doesn't work
nights and mostly not weekends. On day 1 of the weekly window, a Friday-morning working pace
projected through nights and the weekend produced a false "veckokvoten slut sön 10:44". The interim
guard is that the weekly window stays Measuring for its first 24 h.

**Idea.** Learn an activity profile from `window-shape.csv`, which already logs every poll: consumption
deltas bucketed by hour of day (and later by weekday). Forecast = expected consumption integrated over
the *remaining hours* of the window using the profile, scaled by recent intensity, instead of
`rate × remaining minutes`.

- Hour-of-day profile once ≥ 7 days of logs exist; weekday profile once ≥ 21 days exist.
- Mixed sessions: the logs contain consumption from all of the user's sessions and devices (server-side
  totals), which is exactly what the profile should model.
- UI: say so when it applies ("baserat på ditt vanliga mönster").
- Data volume check first: 30 s polls ≈ 2 900 rows/day, so the CSV stays small, but decide on rotation
  before month 2.
- Also consider for the session window: a window spanning the end of the working day should not
  assume work continues into the evening.

## Other open items
- Self-pin the tray icon: done by `scripts/install.ps1`. The app itself does not re-pin if Windows resets it.
- Autostart and install: done via `scripts/install.ps1` (HKCU Run, per-user install dir). Code signing and an update mechanism are still open.
- Light-taskbar legibility: Measuring and Spent icons are nearly invisible on `#f3f3f3` (the user's
  taskbar is dark; low priority).
- `PanelAnchor` reads `NotifyIcon` private fields via reflection. It falls back to bottom-right if a
  .NET update breaks it.
- **Mac needs the transport/data-age freshness split too (2026-09-22).** Windows fixed a live
  false-stale alarm: while burning, the panel read Stale for most of every ~5 min `get_usage`
  refresh cycle, because the frozen `stale_at`/`unknown_at` transport deadlines re-froze only on
  a NOVEL accepted sample, not on the cached duplicates every poll in between actually returned.
  Fix (`docs/forecast-and-states.md`, "Transport liveness vs. data age"): the transport deadlines
  now renew on ANY poll with a valid session reading (novel, cached duplicate, or validly
  closed); only the 20-min fingerprint rule (data age) and the clock-guard anchor still require a
  novel sample. The Mac app's own commit for "a cached duplicate on an open window does not renew
  freshness" (same message as Windows commit 1618146, "An idle account is not a broken one")
  states the same too-narrow rule and needs the equivalent split in
  `src/Mac/Sources/ClaudeQuotaCore/QuotaModel.swift` and `FreshnessOracle.swift`, or it will show
  the same live symptom.

## From the statistics round (2026-09-21)
- **Canonical window key in the live tracker.** Codex statistics finding #19 (the ±120 s jitter
  tolerance can ratchet if every new key is compared to the latest one) was fixed in backfill only.
  `WindowTracker`'s live rollover classification still compares against the most recent deadline.
  Real jitter is sub-second, so this needs a deliberately drifting server to fire, but the two paths
  should agree.
- **Cross-process writer lock.** `CsvFileLock` is in-process. Backfill and a running app are kept
  apart by stopping the app first; a second app instance (or the Mac app on a shared folder) is not
  covered. A named mutex or a lock file would close it.
- **Swift parity for statistics.** The Mac app has none of the cycle archive yet. The byte contract
  and golden files are `docs/statistics.md` and `tests/fixtures/statistics/`.

## Mac parity for the 2026-10-08 round (Windows: loading state, panel header, idle-account freshness)
- **Idle account is not stale.** `src/Mac/Sources/ClaudeQuotaCore/QuotaModel.swift` and `FreshnessOracle.swift`
  need the same two changes as Windows (`docs/forecast-and-states.md`, "An idle account is not a stale
  one"): stop passing a session/weekly `resets_at` deadline to the oracle while the latest poll validly
  reports that window closed, and suspend the 20-min fingerprint rule while the session window is closed
  (`SessionClosed`). Tests: closed session + spent weekly with identical replies for 2 h stays Live; a session
  that ended and now reports closed stays Live; an open session with identical replies > 20 min still goes
  Stale; failures still go Stale/Unknown.
- **Loading state** (grey ring without "!", a travelling arc, "Hämtar kvoten…", 30 s) and the **panel header**
  (title = the account's label, subtitle = plan · organisation) once the Mac app has the slot model.

## From the own-login round (Windows, `docs/multi-account.md`)
- **Mac parity: the same design.** Slots the app owns (`accounts/<id>/config` + `slot.json`), the path
  guard, reconciliation, a login flow that always logs into a new slot, NeedsLogin from the null
  `get_usage` shape, and no follower. Two Mac-specific points: the config-dir path must stay canonical
  (`docs/mac-port.md` §2), and since there is no logout, **deleting a slot must also delete its keychain
  item**, `Claude Code-credentials-<sha256(configDir)[:8]>` (first 8 hex of the SHA-256 of the NFC config
  dir string) -- otherwise a removed account leaves a credential behind.
- **Early warning from `refreshTokenExpiresAt`.** Warn before a login lapses instead of after. It would
  mean reading the credentials file, which the app deliberately never does (it never reads token values);
  deferred until there is a way to get the expiry without touching the token.
- **Does revoking one grant affect another?** Two slots for the same person each hold their own OAuth
  grant. `claude auth logout` revokes server-side, and whether that invalidates the other slot's grant is
  unverified, which is why the app never runs it and removing an account only deletes its folder. If it
  turns out to be safe, "Ta bort…" could also sign the account out.

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

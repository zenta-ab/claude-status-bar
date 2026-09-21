# Cycle archive and statistics — proposal

Keep a permanent record of how every quota window actually ended, so the app can show patterns over
weeks and seasons, calibrate its own forecast, and say something useful about plan size. Based on two
audits: what our own data allows (internal) and how comparable tools do it (external).

## What the audits established

**Nobody in this ecosystem archives cycles.** ccusage, Claude-Code-Usage-Monitor, sniffly,
ccstatusline and claude-powerline all read Claude Code's own logs at render time and show the
*current* block. None keeps completed cycles; none makes a plan-size recommendation with a stated
method. The patterns worth copying come from ActivityWatch, Timing, iStat Menus, WakaTime, RescueTime
and macOS Screen Time.

**Our raw data already carries most of it.** Peak %, final %, whether the ceiling was hit, minutes
blocked, and the window's start time are all derivable from `window-shape-*.csv`, because usage never
truly decreases inside a window. Three things are not in it at all: the plan tier, the forecast's
numbers, and real activity data.

**Coverage is the honesty problem.** The app only logs while it runs. In the existing data one file
has **8.8 % coverage** with a 40-hour hole; since 2026-09-17 coverage is 100 %. Hour-of-day analysis
over a gap misattributes overnight consumption to whenever the app came back. Coverage therefore has
to be a stored, visible number, not an assumption.

**Today's retention contradicts the feature.** `DiskLogSink.CsvRetentionMonths = 6`
(`src/Windows/Data/DiskLogSink.cs:34`) deletes exactly the history a seasonal view would need.

## Decision 1 — three tiers, split by grain, not one store

Retention split by grain is what every comparable tool converges on (Screen Time prunes raw events
after ~4 weeks; iStat Menus averages 6 s samples into 144 s ones for its daily graphs; this repo
already has two different retentions).

| Tier | Grain | Retention | Size / account / year | Purpose |
|---|---|---|---|---|
| `window-shape-YYYY-MM.csv` (exists) | one row per poll | **60 days** (down from 6 months) | ~13 MB at 60 days | debugging, warm start, replay |
| `hourly-YYYY.csv` (new) | one row per hour | forever | ~1 MB | weekday/hour/season patterns |
| `cycles.csv` (new) | one row per closed window | forever | ~0.4 MB | the archive proper |

**Not SQLite.** ActivityWatch, Timing and Screen Time all use it, and it is the right answer at their
volumes — but a year of cycles is ~1 800 rows and a decade is ~18 000. A linear scan is instant, and
CSV keeps the property that matters most here: the file format *is* the contract between the C# and
Swift implementations, already described in `WindowShapeLog.swift` as "the seam that keeps the two
implementations honest". Adding a database means two client libraries, two migration histories and
file locking between two apps, to buy indexes we do not need. If per-poll analytical queries ever
become the point, SQLite is the fallback — this decision is reversible, the data is all replayable.

## Decision 2 — what each new row holds

**`cycles.csv`** — appended when a window closes (a rollover is observed), and backfillable:

```
schema, account_key, window (session|weekly), started_utc, reset_utc, peak_pct, final_pct,
hit_ceiling (0|1), blocked_minutes, covered_minutes, plan_tier, warned_dry_early (0|1),
predicted_peak_pct, predicted_at_utc, ceiling_reached_at, ceiling_censored (0|1)
```

`schema` is now `2` (Codex review 2026-09-21, finding #14) — see "Schema 2" below for the two
added columns and how a schema-1 file already on a real disk is read.

- `covered_minutes` is how much of the window the app actually observed. Everything downstream is
  filtered on it; a cycle at 30 % coverage is listed but never counted in an aggregate.
- `warned_dry_early` / `predicted_peak_pct` / `predicted_at_utc` are the calibration record: what the
  app claimed, and when, versus `peak_pct` — which is what makes "how often is the warning right"
  answerable at all. The numeric estimate is also recomputable offline from raw history, but what the
  panel actually *showed* is not, because hysteresis and freshness were never persisted.
- `plan_tier` can only be captured going forward (≤7 days of Windows raw log can be backfilled;
  macOS keeps no raw log, so that backfill is Windows-only and optional).

**`hourly-YYYY.csv`** — one row per account, window kind and clock hour:

```
schema, account_key, window, hour_start_utc, consumed_pct, samples, covered_minutes
```

`consumed_pct` is the sum of positive deltas in that hour; `covered_minutes` again gates every claim.

### Exact on-disk format (implemented 2026-09-21) — the byte-compatible contract

Both files follow window-shape-\*.csv's own conventions exactly, so a Swift port can read/write
them without a single format decision of its own to make (`src/Windows/Data/CycleArchiveCsv.cs`,
`HourlyRollupCsv.cs`, `CsvUtil.cs` are the reference implementation; `WindowKind` values are
`"session"`/`"weekly"`, lowercase, never the enum's C# spelling):

- **Timestamps** (`started_utc`, `reset_utc`, `hour_start_utc`, `predicted_at_utc`,
  `ceiling_reached_at`): .NET's `DateTimeOffset` round-trip ("O") format, always UTC (written via
  `value.ToUniversalTime()`, so a caller's own non-UTC offset is normalized on write; a reader
  REJECTS a non-zero offset outright rather than silently accepting it — Codex review #21), e.g.
  `2026-09-11T11:20:00.7127900+00:00` — 7 fractional digits, `+00:00` offset, matching
  `DiskLogSink`'s own `utc_iso` column exactly (`entry.At.ToString("O", ...)`). Not the 6-digit
  wire format `window-shape-*.csv`'s `*_resets_at_raw` columns preserve verbatim — `cycles.csv`/
  `hourly-*.csv` store a *parsed* instant (a closed cycle is already a discrete, de-duplicated
  event; there is no live fingerprint left to preserve), so round-tripping through "O" is lossless
  and simpler.
- **Numbers, minutes** (`blocked_minutes`, `covered_minutes`): `InvariantCulture` `double.
  ToString()` — no fixed decimal count, no thousands separator, `.` as the decimal point.
- **Numbers, percentages** (`peak_pct`, `final_pct`, `consumed_pct`, `predicted_peak_pct`):
  Codex review 2026-09-21 #25 — a bare integer when the value is integral, otherwise
  `InvariantCulture` with AT MOST two decimal places (rounded, not truncated;
  `CsvUtil.FormatPercent`) — never `double.ToString()`'s unbounded-precision default.
- **Integers** (`samples`): plain `InvariantCulture` integer text.
- **Booleans** (`hit_ceiling`, `warned_dry_early`, `ceiling_censored`): the literal `1` or `0`
  only — anything else is a malformed row, rejected outright (Codex review #23), never silently
  read as `false`.
- **A missing/unknown value** (`plan_tier` when never observed; `predicted_peak_pct`/
  `predicted_at_utc` when never captured; `ceiling_reached_at`/`ceiling_censored` when
  `hit_ceiling` is false; both prediction fields always, on a backfilled row — see below) is the
  **empty string** between two commas, never `null`, `NaN`, `-1` or a quoted empty string.
- **Quoting**: a field is wrapped in `"..."` (with `""` for an embedded quote) only if it contains
  a comma or a quote. CR/LF are FORBIDDEN outright, never quoted-and-embedded (Codex review #24)
  — `CsvUtil.Field` throws rather than let the line-oriented reader (`StreamReader.ReadLine`)
  silently split a multiline record across two corrupted rows. In practice quoting never fires at
  all: `account_key` is a UUID pair, `plan_tier` a short slug (`max`/`team`/`pro`/…).
- **Line endings**: `\n` only, never `Environment.NewLine` (Codex review #25) — every file ends
  with a final newline. No BOM.
- `schema` is `2` on every row this build writes; a reader dispatches EXPLICITLY on the row's own
  `schema` field (never assumes "unrecognized -> treat as 1") and rejects anything it does not
  recognize outright, rather than silently misreading it (Codex review #22). `account_key` is the account's own directory name under `logs\` (the
  identity key `<accountUuid>_<organizationUuid>`, exactly as already written to disk — see
  `docs/multi-account.md`), so a reader never has to cross-reference the file's own location.
- `cycles.csv` is one file per account directory, appended forever (no rotation — ~0.4 MB/year per
  Decision 1's estimate). `hourly-YYYY.csv` is one file **per calendar year** of `hour_start_utc`,
  named literally `hourly-2026.csv` etc., so no single file grows without bound.
- **Live writes** append one row at a time (`CycleArchiveCsv.AppendRow`/`HourlyRollupCsv.AppendRow`,
  header written once if the file is new, flushed synchronously) via the same per-account
  `DiskLogSink` background queue `window-shape-*.csv` already uses (`DropWrite` under
  backpressure, a logged drop rather than a silent one — see Decision 1's file-format rationale).
  **Backfill writes** rewrite the whole file crash-safely: a temp file in the same directory,
  flushed, then renamed over the real path (`WriteAllAtomically`) — a kill mid-write leaves the
  previous, still-valid file in place.

### `predicted_peak_pct` / `predicted_at_utc` — precisely when they are captured

Captured **once per window**, the first time the app evaluates that window (the always-running 1 s
UI tick, not a poll) at or past **50 % elapsed** (`e ≥ W/2`): `predicted_peak_pct` is the
forecast's `ProjectedPctAtReset` at that instant, `predicted_at_utc` is that instant itself. A
later tick never overwrites it — it is a single snapshot of "what the app believed at the
halfway point", not a running estimate.

**If the app was not running at the 50 % mark** (closed, asleep, or simply not polling that
window) and only resumes after the window has already passed 50 % elapsed, the *first* tick that
observes it there still captures — a slightly late but still-honest reading of "the earliest the
app could tell you". If the app never observes the window at or past 50 % elapsed at all before it
closes (e.g. it was off for the window's whole second half), both fields are left **empty** — there
is nothing honest to report for a prediction the app never actually computed, and a fabricated
"the model would have said X" is exactly the kind of confident-wrong-number this project's
refusal rules exist to prevent (see `docs/forecast-and-states.md`, "Refusal → Measuring").

**Backfilled cycles always leave both empty**, along with `plan_tier` (empty) and
`warned_dry_early` (written `0`, not because the window is known not to have warned, but because
hysteresis history was never persisted and a boolean column has no third "unknown" value to write
— see Decision 5). Only cycles closed by the *running* app from here on carry real calibration
data. A reader must not treat a backfilled row's `warned_dry_early=0`/empty predicted fields as
evidence the warning never fired; it is simply unknowable for that row.

## Decision 3 — what may be claimed, and when

The rule from forecast-verification practice: a bucket with fewer than ~10 observations is not a
claim, and **every statement shows the N behind it** ("based on 8 Tuesdays"). Oura's precedent for the
UI: say "still learning your pattern" rather than show a shaky number.

| Data age | Session window | Weekly window |
|---|---|---|
| 2 weeks | the cycle list and "your peak so far"; no weekday or hour claims | 2 cycles, listed, no aggregate |
| 2 months | weekday claims, marginally, always with the count | first "average peak" and "times you hit the ceiling" |
| 1 year | weekday × hour heatmap; first defensible plan-size statement | month- and season-over-season |

**Pre-specify the slots.** Exactly two recommendations exist, and they are computed for fixed slots
rather than by scanning every weekday × hour × window cell for whatever looks most dramatic — that
scan is the "garden of forking paths" and it manufactures patterns from noise:

1. *"You hit the ceiling N times in the last M weeks, most often <weekday> <hour-range>."*
2. *"You used at most X % of the weekly budget in N of the last M weeks."* — the argument for a
   smaller plan, and its inverse for a larger one.

Both stay hidden until their N threshold is met. No seasonal claim before a full year exists.

### Implemented thresholds and exact tile/recommendation definitions (2026-09-21)

`src/Windows/Model/StatisticsEngine.cs` is the pure, UI-free reader (Decisions 1–3 only; Decision
4's UI is a separate, later piece of work). Two constants gate every single result it produces,
uniformly:

- **Coverage threshold — 80 %** (`StatisticsEngine.CoverageThreshold`). A cycle counts only if
  `covered_minutes / window_minutes ≥ 0.8`; an hour counts only if `covered_minutes / 60 ≥ 0.8`.
  Filtered out *before* any aggregate is taken — an 80 %-covered cycle is not down-weighted, it is
  simply not there for this computation.
- **N minimum — 10 observations** (`StatisticsEngine.MinObservations`), the same "~10" this
  decision's own rule states, applied as one uniform floor to every tile's and every
  recommendation's *own* qualifying population, counted in that population's own natural unit —
  a qualifying session cycle for the cycle-based tiles/recommendations, but **for the peak-weekday
  and peak-hour tiles specifically, a distinct covered local day of that weekday/hour, never an
  hourly-\*.csv row** (fixed 2026-09-21, see "N is counted in the wrong unit" below): with dense
  continuous polling a single weekday can rack up dozens of hourly rows well before ten actual
  occurrences of that weekday exist, so counting rows silently unlocked the claim far earlier than
  ten real observations of the thing being claimed. The floor is also, for these two tiles only,
  **per bucket**: every weekday/hour with at least one observation ("competing for peak") must
  individually clear it, not just the winner — a thin weekday could otherwise win the argmax
  purely from having too little data to regress toward the mean.

The five named tiles (Decision 4's UI list) and their exact source/scope, so a Swift port matches
byte-for-byte behaviour, not just the shape:

| Tile | Source | Scope | Definition |
|---|---|---|---|
| Peak weekday | `hourly-*.csv` | **session** kind only, UTC weekday | mean `consumed_pct` grouped by `hour_start_utc`'s UTC weekday; the highest mean wins (ties broken by earliest weekday); `N` shown is the winning weekday's own distinct-covered-local-day count (e.g. "12 fredagar"), not the row count |
| Peak hour | `hourly-*.csv` | **session** kind only, UTC hour-of-day | same, grouped by UTC hour 0–23; `N` shown is the winning hour's own distinct-covered-local-day count (e.g. "14 dagar") |
| Times at the ceiling | `cycles.csv` | **session** kind | count of qualifying cycles with `hit_ceiling=1` |
| Share of weekly budget used | `cycles.csv` | **weekly** kind | mean `peak_pct` across qualifying weekly cycles |
| Forecast hit rate | `cycles.csv` | **both** kinds, pooled | among qualifying cycles with `warned_dry_early=1` (the denominator — a cycle with no warning is neither a hit nor a miss), the fraction with `hit_ceiling=1` |

Peak weekday/hour are scored from **session** hourly rows only (the far more frequent, less
decay-confounded signal).

### Local-time bucketing (fixed 2026-09-21 — the engine used to bucket in UTC)

`cycles.csv`/`hourly-*.csv` store every instant in UTC, and every threshold/coverage/hit-rate
number above stays computed in UTC — `peak_pct`, `hit_ceiling` and `covered_minutes` carry no
timezone at all, so there is nothing to convert. But a *working pattern* — which weekday, which
hour of day — is meaningless in UTC to a person who lives in one timezone: a user in Stockholm
sees "peak hour 20 UTC" shift by 1–2h across every DST boundary, which is not the pattern they
actually have. `StatisticsEngine.Compute`/`ComputeForAccount` therefore take a `TimeZoneInfo`
(mirroring `Ui/PanelText.Compose`'s own `(view, now, tz)` shape) and use it to bucket exactly
three results in **local** wall-clock time: the peak-weekday tile, the peak-hour tile, and the
ceiling recommendation's "most often ⟨weekday⟩ ⟨hour⟩" clause. A null `tz` defaults to
`TimeZoneInfo.Utc` (used by every pre-existing UTC-fixture test, and any caller that genuinely
wants UTC); the statistics window passes `TimeZoneInfo.Local`.

Store stays UTC, bucketing happens in local time — the same split every other local-time surface
in this app (`PanelText`) already uses; only the *interpretation* of an already-recorded UTC
instant changes, never what gets written to disk.

**DST is handled by construction, not by a special case.** `TimeZoneInfo.ConvertTime(utc, tz)` is
always well-defined for a UTC instant — every UTC instant maps to exactly one local wall-clock
reading. (Only the reverse direction, local → UTC, is ambiguous/skips values across a change —
irrelevant here, since every bucketed value already started life as UTC.) Concretely, across
Europe/Stockholm's October fall-back: two distinct UTC hourly rows (`hourly-*.csv`'s grain is one
row per UTC clock hour) both convert to local hour 02 on the same calendar day — correctly, since
that clock hour genuinely occurred twice that day, so both real hours of usage count. Across the
March spring-forward, no UTC hourly row ever converts to the skipped local hour — it was never
observed because it never existed, not a vanished observation. Neither case needs an explicit
guard: the fix is entirely in converting *before* bucketing, not in adding DST-specific logic
around it.

The two recommendations, exact slots:

1. **Ceiling recommendation** — "*You hit the ceiling N times in the last M weeks, most often
   \<weekday\> \<hour\>.*" Source: qualifying **session** cycles. `N` = count with
   `hit_ceiling=1`; the *pool* gating readiness is every qualifying session cycle (≥10 before
   anything is shown, even "0 times"). `M` = weeks spanned by the qualifying pool
   (`ceil(days between earliest and latest reset_utc / 7)`, minimum 1). The "most often" weekday/
   hour is read off `reset_utc − blocked_minutes` (the moment the ceiling was actually reached,
   not the reset itself) for the `hit_ceiling=1` cycles, grouped by (UTC weekday, UTC hour),
   highest count wins; left absent when the count is 0 — there is no "most often" to name.
2. **Weekly budget recommendation** — "*You used at most X % of the weekly budget in N of the
   last M weeks.*" `X` is **pre-specified, fixed at 50 %**
   (`StatisticsEngine.WeeklyBudgetDownsizeThresholdPct`) — not a scan for whatever threshold looks
   best. `N` = count of qualifying weekly cycles with `peak_pct ≤ 50`; `M` = the qualifying weekly
   cycle pool size (each weekly cycle is one week by construction). Read the other way — a low `N`
   relative to `M` — the same number argues against downsizing (Decision 3's "inverse for a larger
   one"); this is one computation, read both directions, not two separate slots.

A CLI entry point (`ClaudeStatusBar.exe --backfill-statistics`) and an automatic once-per-install
background pass (gated by a `.statistics-backfilled` marker file next to `logs\`, so an ordinary
restart never re-scans months of CSVs) both call `StatisticsBackfill.Run` — see Decision 5.

## Decision 4 — where it lives in the UI

Three tiers, the way Windows' own battery report separates recent detail from long-run capacity:

1. **Current cycle** — today's panel, unchanged. It must stay fast to read.
2. **Recent cycles** — a list of the last ~2 weeks: one line per cycle with a small burndown sparkline,
   peak, and whether it hit the ceiling. Reached from the tray menu, in its own window.
3. **Long run** — 4–6 named tiles first (peak weekday, peak hour, times at the ceiling, share of the
   weekly budget used, forecast hit rate), with a GitHub-style calendar heatmap of "days you hit the
   ceiling" underneath. Fixed presets (last 2 weeks / 3 months / year), not a zoomable chart.

Leading with named tiles rather than a dense chart is WakaTime's and RescueTime's lesson; fixed
presets over infinite zoom is iStat Menus'.

**Proactive advice**: at most one line in the panel, only when a recommendation has passed its N
threshold and changed since last shown. Everything else waits until the statistics window is opened.

## Decision 5 — backfill

Derive `cycles.csv` and `hourly-*.csv` from the CSVs already on disk, once, at first run after the
update, and re-derivable on demand (`src/Windows/Data/StatisticsBackfill.cs`):

- Group rows by jitter-tolerant `resets_at` exactly as `CsvReplay` does (the same ±120 s tolerance
  `WindowTracker.JitterTolerance` uses for live "same window" membership); a forward jump > 120 s
  closes a cycle and starts the next group. A *regressed* key (>120 s earlier than the current
  group's) is replica noise on the key itself — ignored unconditionally, never joins any group,
  exactly like the live tracker's `RegressedIgnore` outcome. The **last** group in the data (per
  window kind) is never emitted as a closed cycle, and the **last** clock-hour bucket is never
  emitted as a closed hour — neither has a confirmed close in the data on hand, the same "only
  closed things get archived" rule live tracking already follows.
- `covered_minutes` (per cycle and per hour) sums the actual gaps between consecutive poll
  timestamps in the group/bucket, each capped at 10 minutes (comfortably above every real poll
  cadence — idle 150 s, spent 300 s — but small enough that a genuine multi-hour gap still shows
  as one) — so thin history is visibly thin instead of silently wrong. This never routes through
  `WindowTracker` itself (which exists to seed the *live* rate/envelope state, not the archive);
  `StatisticsBackfill.GroupWindow` is a standalone pure function mirroring the same rules.
- **Backfill / legacy directories.** Only real per-account directories directly under `logs\` are
  scanned — a name shaped `<accountUuid>_<organizationUuid>` (`StatisticsBackfill.
  IsBackfillableAccountDirectory`). Two things are skipped **entirely**, never even counted:
  - The **legacy pre-split directory**: a **bare accountUuid with no `_organizationUuid` suffix**.
    Before the identity-guard fix (`docs/multi-account.md`) kept per-account state keyed on the
    *(accountUuid, organizationUuid)* pair, it was keyed on accountUuid alone — so one person's
    Team seat and personal Max seat (the same accountUuid, different organizations) could share
    one directory, and that mixing cannot be split apart after the fact.
  - `logs\_pending\` (identity not yet known when those polls landed — not a stable per-account
    key either) and the **top-level `window-shape*.csv` files** directly under `logs\` (predating
    per-account directories altogether) — the directory scan only ever looks at subdirectories, so
    these are never visited in the first place.

  Verified against this machine's own real, pre-existing data: `logs\<uuid>\` (bare, from before
  the multi-account split) and `logs\window-shape-2026-09.csv` / `logs\window-shape.csv`
  (top-level) all sit alongside two real `logs\<uuid>_<uuid>\` directories — backfill produced
  `cycles.csv`/`hourly-2026.csv` only for the two real directories and never touched the legacy
  ones (see the task's VERIFY step 3 report for the exact counts).
- **Idempotent**: every run derives the same candidates from the same on-disk data and merges them
  into any existing file by `(account_key, window, reset_utc)` / `(..., hour_start_utc)` — a
  candidate whose key already exists is never re-added. Running `Run()` twice in a row writes
  nothing the second time. This also means backfill is safe to re-run after live tracking has
  already appended real rows: it only ever *adds* rows the live path hasn't recorded yet, never
  overwrites or removes one.
- **Crash-safe**: every actual file write goes through `CycleArchiveCsv`/`HourlyRollupCsv`'s
  `WriteAllAtomically` — a temp file in the same directory, flushed, then renamed over the real
  path. A kill mid-write leaves the previous, still-valid file exactly as it was.

## Decision 4, implemented (2026-09-21) — the statistics window

`Ui/StatisticsText.cs` (pure text, unit-tested in `StatisticsTextTests.cs`) and `Ui/
StatisticsForm.cs` (the hand-drawn window itself) implement Decision 4. Points the decision
above states as intent but doesn't pin to an exact mechanism:

- **Period filtering.** The three presets filter both the named tiles and the heatmap by
  `StartedUtc`/`HourStartUtc >= now - period` (`Data/StatisticsDataLoader.PeriodCutoff`,
  `StatisticsEngine.PeriodSpan`: 14/90/365 days). **Recent cycles stays fixed at the last ~14
  days regardless of the period selector** — it is a separate, always-on view per the decision's
  own "Recent cycles: the last ~2 weeks" wording, not one more thing the presets control.
- **Heatmap scope and bucketing.** Session cycles only (a weekly cycle spans 7 days and has no
  single day of its own to attribute a ceiling hit to — same scope PeakWeekday/PeakHour already
  use). One cell per LOCAL calendar day of the cycle's `StartedUtc`; a day with no session cycle
  at all is `NoData`, a day whose cycles never hit the ceiling is `Normal`, a day with at least
  one `hit_ceiling=1` cycle is `HitCeiling` — three colour steps, matching "few colour steps,
  highlighting days that hit the ceiling". Laid out GitHub-style: one column per week, one row
  per weekday (Monday=row 0 .. Sunday=row 6).
- **Recent-cycle incomplete marker.** Reuses `StatisticsEngine.CoverageThreshold` (80%) — the
  same bar a cycle must clear to count toward any tile's N — rather than inventing a second
  threshold just for display: a cycle below it is listed (never hidden) with "ofullständig —
  appen körde X % av tiden".
- **Sparkline data source.** `Data/RecentCycleSparkline.cs` reads the raw `window-shape-*.csv`
  rows for a cycle's own time range via `CsvReplay.FilterForWindow` (the exact jitter-tolerant
  window-membership rule `QuotaModel.WarmStart` already uses) and takes a running-max envelope
  over them, so the burndown shape matches what the panel itself would have shown live. A cycle
  older than the 60-day raw retention, or a demo/synthetic account with no raw files at all,
  yields no points — the sparkline then draws a faint dashed placeholder rather than either a
  fabricated shape or a silently empty box.
- **Proactive advice (panel line).** Recomputed in `AccountRuntime.ArchiveClosedWindowsAndHours`
  only when a cycle actually just closed (a rollover is rare — a few times a day at most), never
  on the 1 Hz eval tick or every poll: reading `cycles.csv`/`hourly-*.csv` back off disk on every
  tick would be wasteful for a number that can only change when new data lands. `Model/
  StatisticsAdvice.Decide` fires when either recommendation's own "signature" (the fields that
  make it a materially different statement, not merely "still true") differs from what
  `Data/StatisticsAdviceStore.cs` last persisted for that account (`%LOCALAPPDATA%\
  ClaudeStatusBar\statistics-advice.json`, next to `accounts.json`, same write-temp-then-rename
  discipline). The panel line latches — it keeps showing the same text on every render until a
  NEW signature supersedes it; "shown once" means the underlying pattern is only ever announced
  once, not that the line vanishes after one frame.
- **Demo data (task item 3).** `Model/StatisticsDemoData.cs` builds two scenarios as real
  `CycleArchiveCsv.Row`/`HourlyRollupCsv.Row` values (every timestamp built from a LOCAL
  wall-clock instant converted to UTC, exactly like the real app) run through the real engine —
  never a hand-picked UI state. `StatusBarApplicationContext.BuildDemoStatisticsAccountRefs`
  writes them to a scratch directory under `Path.GetTempPath()` (never the user's real log
  directory) via the same `CycleArchiveCsv`/`HourlyRollupCsv` writers the live app uses, so the
  statistics window's normal disk-reading path is exercised unchanged, with no demo-only branch
  inside the window itself.
- **Capture mechanism.** `StatisticsForm.CaptureFullContent` resizes the window to fit its own
  full (unscrolled) content, then `DrawToBitmap`s it. Reading `Handle`/calling `CreateControl()`
  alone left every child control (labels, buttons, the combo box, the content panel) blank —
  `DrawToBitmap`'s `WM_PRINT` only paints children correctly once the window has actually been
  shown at least once. The window is therefore moved off-screen (`Location = (-32000, -32000)`)
  and `Show()`n before capturing, then `Hide()`n again — `Show()` itself does not depend on
  desktop compositing or an unlocked session the way `CopyFromScreen` does, which is the whole
  reason this task specified `DrawToBitmap` over the panel's own screen-capture path.

## Decisions from the owner (2026-09-21)

The two open questions above are now decided:

1. **Raw `window-shape-*.csv` retention drops from 6 months to 60 days**
   (`DiskLogSink.CsvRetentionDays = 60`, replacing the old `CsvRetentionMonths = 6`). The long run
   moves to `cycles.csv`/`hourly-*.csv` (retained forever, never touched by this sweep), so nothing
   is lost for statistics — but replaying a window-shape row older than 60 days for debugging
   becomes impossible (warm start itself only ever looks back one calendar month anyway, so this
   does not affect startup behaviour).
2. **The statistics UI will be hand-drawn**, like the panel already is — no chart library,
   keeping the zero-dependency property both apps have today. Nothing built against this decision
   yet: Decision 4 (the UI itself) is explicitly out of scope for this pass and is a separate,
   later piece of work for whichever builder picks it up next.

## Four defects found against real user data, fixed (2026-09-21)

Found by opening the statistics window against a real account's ~2 weeks of history rather than
demo data. All four are in `StatisticsEngine`/`StatisticsText`/`StatisticsAdvice`/
`StatisticsDataLoader`; the fix and its exact numbers are recorded here per Decision 3/4 above,
which each of these directly amends.

1. **N was counted in the wrong unit for the peak-weekday/peak-hour tiles** — covered above,
   under "Implemented thresholds" and the tile table: `N` is now distinct covered local days of
   the weekday/hour being claimed, never the raw `hourly-*.csv` row count, and readiness requires
   every competing weekday/hour to individually clear `MinObservations` in that same unit. Below
   the threshold, the tile still names its current leading candidate ("Samlar data — 2 av 10
   fredagar" / "... dagar" for the hour tile) rather than the vaguer prior wording, so the
   learning state itself carries the same honesty ("here's what we have so far, and it isn't
   enough yet") the rest of this decision already asks for.

2. **A "0 times" ceiling recommendation is not advice.** The ceiling recommendation
   (`ComposeCeilingRecommendation`/`StatisticsAdvice.CeilingSignature`) is now hidden entirely
   when `TimesHit = 0` — the "Times at the ceiling" tile already states "0 gånger" honestly; a
   green recommendation card repeating the same zero is not a recommendation, it is noise. Swedish
   wording is also now genuinely singular/plural-correct for small counts and periods
   (`SwedishText.Times`/`Weeks`/`RecentWeeksPhrase`): "1 gång" never "1 gånger", "senaste veckan"
   never "de senaste 1 veckorna" (`WeeksSpanned` can legitimately be 1 for a sparse or
   newly-started account).

3. **The weekly-budget recommendation now states its conclusion**, per this decision's own "the
   argument for a smaller plan, and its inverse for a larger one" — previously it stopped at the
   raw statistic. Exact thresholds (`StatisticsEngine.WeeklyBudgetRecommendation.Direction`,
   computed once per qualifying weekly-cycle pool, read both directions from the same numbers per
   this decision's "one computation, not two slots"):
   - **Downsize** — at least `WeeklyBudgetDownsizeSharePct` = **70 %** of qualifying weeks sat at
     or below the existing `WeeklyBudgetDownsizeThresholdPct` (50 %), **and** no qualifying weekly
     cycle in the same pool has `hit_ceiling=1`. Conclusion appended: "— en mindre plan skulle
     troligen räcka för hur du arbetar nu." The ceiling-hit guard is absolute, never overridden by
     however high the at-or-below share is — a period that also hit the ceiling is never honest
     grounds to suggest a smaller plan.
   - **Upsize** — at least `WeeklyBudgetUpsizeSharePct` = **50 %** of qualifying weeks reached
     `WeeklyBudgetNearCeilingPct` = **90 %** or more of the weekly budget ("at or near the
     ceiling"). Conclusion appended: "— du slår ofta i taket; en större plan eller jämnare
     fördelning skulle hjälpa." Checked only when Downsize's own condition doesn't hold (the two
     are mutually exclusive by construction: a week can't be both ≤50 % and ≥90 %).
   - **Neither threshold clears** (the neutral middle, e.g. an even split with no ceiling hits) —
     `Direction = None`, and the whole recommendation card is hidden, same as before it reached
     its N threshold — a bare, conclusion-less statistic is exactly what this fix removes, so the
     neutral case gets no card at all rather than a card with nothing to say.

4. **The account selector now shows the same label the tray/panel already show**
   (`StatisticsDataLoader.ResolveAccountLabel`), not the email local part. Root cause: on a
   `--capture-statistics` run, `StatusBarApplicationContext.RunStatisticsCapture` builds the
   statistics window *before* `Application.Run` ever pumps a message (deliberately — `DrawToBitmap`
   itself needs no message loop), but `AccountRuntime.PollAsync`'s completion lands on the UI
   thread via a queued window message that only runs while something pumps the queue — so the
   live `SubscriptionType` never arrived in time, and `AccountLabel.Resolve`'s chain fell all the
   way through to the bare email local part. Fixed in two parts: `RunStatisticsCapture` now pumps
   with `Application.DoEvents` (never `Application.Run` — that would start a second, nested loop)
   for up to ~3 s waiting for each configured account's first poll, then re-runs the whole-set
   disambiguation pass, before building the window — for a normal capture this alone makes
   `AccountRuntime.Label` correct and disambiguated, identical to what the tray already shows.
   `ResolveAccountLabel` is the belt-and-suspenders fallback for an account whose channel still
   hasn't come up within that wait: it returns the live label completely unchanged whenever a live
   plan tier is known (never recomputing away a disambiguated label), and only when it truly isn't
   falls back to the plan tier already recorded on that account's own `cycles.csv` from a past
   live poll — "`AccountLabel` with the data available on disk", never the on-disk fallback for an
   account this call already has live confirmation for.

## Codex review round (2026-09-21) — 32 findings, fixed

`docs/reviews/2026-09-21-codex-statistics.md` is the review and its decisions table; this section
records what changed in this codebase and in the contract because of it. Governing rule for every
judgement call in this round: **a statistic the app cannot defend is worse than no statistic** —
where a fix would weaken a claim, the claim is weakened (or removed) rather than left overstated.

### Re-baselining and the continuity allowance (#3, #17) — what every aggregate is now built from

The single most consequential fix. `WindowTracker` (live) and `StatisticsBackfill.GroupWindow`
(historical) both now share one concept, `ContinuityAllowanceMinutes = 10` (comfortably above
every real poll cadence — idle 150s, spent 300s — but small enough that a genuine gap still shows
as one):

- **Coverage** (`covered_minutes`, per cycle and per hour): the gap between two consecutive polls
  counts in full when it is `<= ContinuityAllowanceMinutes`, and contributes **zero** when it is
  larger — never capped down to the allowance. The old behaviour (`Math.Min(gap, cap)`) fabricated
  up to 10 minutes of "coverage" for an outage of any length; a 60-minute outage used to read as
  10 covered minutes, now reads as 0.
- **Consumption** (`consumed_pct` on `hourly-*.csv`): the FIRST observation of a window — a cold
  start, or the first poll right after a rollover — and the first observation after a gap beyond
  the continuity allowance, **re-baseline** the running envelope: the jump still raises the
  envelope (so peak/ceiling tracking stays correct), but it is never credited as consumption to
  whichever hour the app happened to resume in. Concretely: the app starting at 14:00 already at
  60% no longer shows `consumed_pct=60` for hour 14; a 10%->80% jump across a multi-hour outage no
  longer attributes 70 points to the hour observation resumed in. A genuine small rise observed
  under continuity (e.g. +5% five minutes later) is still credited exactly as before.

Both engines re-derive this independently (`StatisticsBackfill.GroupWindow` deliberately never
routes through `WindowTracker` -- see that class's own `isReplay` doc note), so the fix had to be
made twice, with matching tests (`StatisticsTests.cs`, "Live cycle detection" and "Backfill
grouping" sections) proving the same concrete review inputs against both.

### `ceiling_reached_at` / `ceiling_censored` -- schema 2 (#14)

`cycles.csv`'s `schema` is now `2`. Two columns were appended (after `predicted_at_utc`, so the
first 14 columns are byte-identical to schema 1):

- `ceiling_reached_at` -- the UTC instant the envelope first reached 100% this window, empty when
  `hit_ceiling=0`.
- `ceiling_censored` (`0`/`1`) -- whether that instant is **exact** (reached under continuous
  observation, i.e. the poll that revealed it was not itself a re-baseline per #3/#17 above) or
  **interval-censored** (the crossing happened somewhere inside an unobserved gap, so the true
  instant is only known to lie within that interval). `blocked_minutes` is computed the same way
  either way (`reset_utc - ceiling_reached_at`) but is only a LOWER BOUND while censored.

A schema-1 file (written by the previous build -- these exist on real disks right now) is migrated
on read, not rewritten in place: `ceiling_reached_at` is derived the old way
(`reset_utc - blocked_minutes`) when `hit_ceiling=1`, and the row is unconditionally marked
**censored** -- schema 1 never recorded whether its own derivation crossed a gap, so a migrated row
can never honestly claim exactness it never verified. `CycleArchiveCsv.TryParse` dispatches
explicitly on the row's own `schema` field (`"1"` or `"2"`); anything else is rejected outright
(#22), never silently parsed as one of the two known shapes.

### Plan-size advice: both windows, the current plan only, and a two-stage rule (#1, #2, #5)

`StatisticsEngine.WeeklyBudgetRecommendation` -- the "smaller/larger plan" card -- now requires all
of the following before it will name a direction:

1. **A known current plan tier** (#2). The "current tier" is the `plan_tier` of the most recently
   closed cycle (session or weekly, whichever is more recent) that recorded one at all. An account
   with no live-tracked cycle yet -- a 100% backfilled/legacy history, `plan_tier` always empty --
   has no known current tier, and gets no plan advice at all; this falls out naturally from an
   empty qualifying pool (N=0), no separate flag needed.
2. **Only same-plan cycles** (#2). The qualifying pool is the TRAILING run of weekly cycles (by
   `reset_utc`) that all share the current tier -- N restarts the instant the tier changes,
   scanning backward from the most recent cycle; a backfilled row (`plan_tier` always empty)
   always breaks the run. Percentage-of-budget is not comparable across a plan change.
3. **The session window checked too, for Downsize specifically** (#1). Downsizing is forbidden if
   any qualifying session cycle overlapping the same time span as the weekly pool hit the ceiling,
   or if there is no qualifying session evidence at all for that span -- the weekly-budget number
   alone says nothing about whether the tighter five-hour session ceiling is being hit daily.
   Upsize has no such guard (there is no symmetric risk to naming a larger plan from weekly data
   alone). The composed card's secondary line names both pools actually evaluated for a Downsize
   verdict ("baserat på N veckor och M sessioner (femtimmarstaket kontrollerat)").
4. **A two-stage confidence rule** (#5). `N >= 10` same-plan qualifying weeks alone (plus the
   session guard passing) unlocks only a HEDGED early hint -- the composed line is prefixed
   "Tidig indikation, baserat på N veckor: ..." -- never the plain, unhedged statement. The plain
   statement additionally requires the same-plan pool's own span
   (`StatisticsEngine.PlanAdviceFullConfidenceDays = 365`) to reach a full year. This replaces the
   previous single N>=10 gate, which exposed a plan-size claim after roughly 2.5 months while the
   doc's own data-age table promised "first defensible plan-size statement" at one year.

The Upsize wording itself changed too (#9): it now states ITS OWN evidence -- weeks at/near the
ceiling (`>= 90%` of the weekly budget) -- never the Downsize framing's numbers, and never says
"slår i taket" (hits/reaches the ceiling), which is reserved for a real `hit_ceiling=1` event
(`TimesAtCeiling`/`CeilingRecommendation`). Reaching 90% of the weekly budget is being NEAR the
ceiling, not hitting it.

### The ceiling recommendation no longer scans weekday x hour (#6)

`StatisticsEngine.CeilingRecommendation` dropped `MostOftenWeekday`/`MostOftenHour` entirely -- the
168-cell argmax this decision's own "garden of forking paths" rule forbids, and which a single
observation could win outright (finding #6's concrete input: one hit among ten qualifying cycles
"won" the old scan). The card now states only the count and the span
("Du nådde taket N gånger de senaste M veckorna."). Weekday/hour patterns are still available --
from the separate, independently N-and-coverage-gated `PeakWeekday`/`PeakHour` tiles only.

### Forecast calibration: two numbers, not one "accuracy" (#7, #8)

The single `ForecastHitRateTile` (labelled "accuracy") is replaced by two independently-gated
tiles, `ForecastPrecisionTile` ("Varningar som stämde" -- of the warnings shown, how many were
followed by a ceiling hit) and `ForecastRecallTile` ("Takträffar som varnades" -- of the ceiling
hits that happened, how many were warned in advance). The old single number was precision alone
and could read "100% rätt, baserat på 10 varningar" while 90 unwarned ceiling hits went
unmentioned (finding #7's exact input). Both tiles pool over **forecast-eligible** cycles only
(#8): both prediction fields present, `predicted_at_utc` inside `[started_utc, reset_utc]`, and
strictly before the outcome (`reset_utc` for a miss, the -- possibly censored -- ceiling-reached
instant for a hit). A cycle with `warned_dry_early=1` but empty prediction fields (corruption, an
older writer, a partial migration) no longer counts as eligible for either number.

### Peak-weekday/peak-hour: local-day totals, and a real comparison (#10, #11, #16)

Both tiles now aggregate hourly rows to LOCAL-DAY TOTALS first (summing every row landing on the
same local calendar date within a bucket), THEN average across those distinct-day totals -- never
a flat mean of the raw rows. This is what makes a weekday with few-but-large observations correctly
beat one with many-but-small ones (finding #10: ten Mondays at one covered hour each, 50% that
hour, correctly loses to ten Tuesdays at eight covered hours each, 10% each -- 50% vs 80% per day,
where the old row-mean read it backwards), and it is also what makes a DST fall-back's two repeated
local hours collapse into ONE observation for the day-count while still summing their genuinely
separate consumption into that day's total (finding #16/#32 -- the old code averaged the two rows
in separately, dividing by 11 while displaying N=10).

Readiness originally required (#11) only that at least two distinct buckets be represented in the
data at all -- a single observed hour/weekday is not a comparison, it is the only data point that
exists (finding #11's concrete input: the app runs only 14:00-15:00 on ten days and is closed the
rest of the time; there is only ever one hour bucket, and "most active hour 14-15" compares it
against nothing). That rule was too weak on its own, and was replaced 2026-09-21 -- see "The
12-of-24 / 5-of-7 quorum" below.

### The 12-of-24 / 5-of-7 quorum (2026-09-21 follow-up to #11) -- "≥2 buckets" replaced

The original "≥2 qualifying buckets" fix closed finding #11's exact degenerate case (one single
observed hour) but not the general one: an app that only ever ran during hours 14-16 could still
satisfy "≥2 qualifying buckets" once both hour 14 and hour 15 individually cleared
`MinObservations`, and claim "peak hour 14" while the other 21 hours of the day were never
observed at all -- exactly the kind of comparison-against-nothing the rule was meant to prevent,
just needing one more qualifying bucket to slip through.

Replaced with a quorum on the number of QUALIFYING buckets themselves, not merely "≥2 of them":

- **Peak hour** requires at least `StatisticsEngine.PeakHourMinQualifyingBuckets` = **12** of the
  24 local clock hours to EACH individually clear `MinObservations` (10 distinct covered local
  days for that hour) -- not just any 2. Below 12, the hour tile shows the learning state with
  honest PROGRESS wording ("Samlar data -- 7 av 12 timmar med tillräcklig data"), naming no
  candidate hour at all (naming one before a real 12-way comparison exists is exactly the
  overstatement this rule closes).
- **Peak weekday** requires at least `StatisticsEngine.PeakWeekdayMinQualifyingBuckets` = **5** of
  the 7 local weekdays, same rule, same learning-state wording ("3 av 5 veckodagar med tillräcklig
  data").
- The winner in either tile is chosen ONLY from the qualifying set, and is itself always one of
  its members -- a below-threshold bucket can never win by construction.
- **Scope wording.** A claim made over fewer than the full bucket space says so: "kl 14–15 — av de
  14 timmar datorn var igång" (hour) / "tisdag — av de 5 veckodagar med data" (weekday). The
  clause is omitted entirely only when EVERY bucket in the space qualified (all 24 hours, or all 7
  weekdays) -- the ordinary case is scoped, because most real usage patterns never touch every
  hour of the day or every day of the week even once they clear the quorum.

**Why 12/24 and 5/7, not literal "every bucket must be represented"** (this decision's own
deviation from the review's most literal reading, carried forward from the original #11 fix):
requiring literal full-day/full-week coverage would make these two tiles nearly unreachable for
any real, focused usage pattern -- a user whose computer is never on at 4 AM, or who never works
Sundays, would never see a peak-hour/peak-weekday claim at all, however much real data
accumulates. A majority quorum (half the space, rounded up for weekdays; exactly half for hours)
still directly kills the review's degenerate examples -- a narrowly-focused pattern confined to 2-3
hours or 1-2 weekdays stays in the learning state indefinitely, honestly -- while keeping the
feature reachable for the far more common case of "on most hours/days, off at night/weekends".
This keeps the governing rule's own "prefer the learning state, not an unreachable one" spirit
(see "Deviations from the review's own 'smallest fix'" below, which this section now supersedes
for #11 specifically).

### The heatmap: coverage-gated, and hits dated to when they happened (#12, #13)

The GitHub-style calendar heatmap now has four states, not three: `NoData` (no session cycle that
day), `Incomplete` ("okänd", a neutral colour -- a session cycle existed that day but never cleared
`StatisticsEngine.CoverageThreshold`), `Normal`, and `HitCeiling`. A single-sample, near-zero-
coverage cycle that happened to read 100% can no longer paint the same red cell as a fully-observed
day (#12). Separately, a ceiling hit is now dated to the LOCAL DAY the ceiling was actually reached
(`ceiling_reached_at`, converted to local time), never the cycle's start day -- a session starting
Monday 22:00 that reaches 100% Tuesday 02:00 marks Tuesday, while Monday still shows its own
(non-hit) activity if it had any (#13).

### CSV format: the byte contract, hardened (#21-#25, #30)

The exact on-disk contract both `CycleArchiveCsv`/`HourlyRollupCsv` (C#) and the future Swift port
must reproduce byte-for-byte:

- **Encoding/line endings**: UTF-8, no BOM, `\n` only (never `Environment.NewLine`'s `\r\n` on
  Windows), every file ends with a final newline.
- **Timestamps**: `DateTimeOffset` "O" round-trip format, but `ToUniversalTime()`d before
  formatting -- always UTC, never a caller's own local offset (#21). A reader now REJECTS a
  timestamp with a non-zero offset outright, rather than silently normalizing it.
- **Percentages** (`peak_pct`, `final_pct`, `consumed_pct`, `predicted_peak_pct` -- never
  `blocked_minutes`/`covered_minutes`, which are plain minute counts): written as a bare integer
  when the value is integral, otherwise `InvariantCulture` with AT MOST two decimal places
  (rounded, not truncated) -- never .NET `double.ToString()`'s unbounded-precision default, which a
  Swift numeric formatter need not reproduce digit-for-digit (#25).
- **Booleans** (`hit_ceiling`, `warned_dry_early`, `ceiling_censored`): the literal `1` or `0`
  only -- anything else (`"true"`, `"x"`, ...) is a malformed row, rejected outright, never
  silently read as `false` (#23).
- **Schema dispatch**: a reader branches explicitly on the row's own `schema` field and rejects an
  unrecognized value outright (#22) -- see "Schema 2" above.
- **CR/LF are forbidden** in `account_key`/`plan_tier` (the only free-text fields) -- `CsvUtil.
  Field` throws rather than silently emitting a multiline record the line-oriented reader
  (`StreamReader.ReadLine`) would then split and corrupt (#24).

**Golden fixtures** (#25, #30): `tests/fixtures/statistics/cycles-golden.csv` and
`hourly-golden.csv` are hand-written reference files exercising every column, including the empty/
unknown cases and a value requiring percentage rounding. `StatisticsTests.cs`'s
`CycleArchiveCsv_GoldenFixture_FormatLineIsByteIdenticalToTheSharedContractFile`/
`HourlyRollupCsv_GoldenFixture_...` build the identical rows in C# and assert the formatted output
is byte-for-byte identical to these files -- these fixtures, not the C# round-trip tests alone, are
what a Swift implementation must reproduce exactly.

### Directory validation, the writer lock, and dedup (#4, #19, #20, #26)

- **`StatisticsBackfill.IsBackfillableAccountDirectory`** now requires the directory name to be
  EXACTLY two UUIDs joined by one underscore (`Guid.TryParseExact` on both halves) -- the old
  check (any name containing an underscore) wrongly accepted `cache_old`, `foo_bar`, or
  `uuid_not-a-uuid` as if they were real per-account directories (#26).
- **One writer lock per account** (`CsvFileLock`, keyed by the account's log directory) is now
  shared by every live append (`CycleArchiveCsv`/`HourlyRollupCsv.AppendRow`) and every backfill
  rewrite (`StatisticsBackfill`'s read-merge-write, which holds the SAME lock across its own read
  AND write) -- a live row landing between backfill's read and write can no longer be silently
  dropped (#4).
- **Window grouping compares against a stable canonical key** (`StatisticsBackfill.GroupWindow`):
  each group's classification is measured against its own FIRST observed deadline, never the most
  recently jittered one -- a chain of small jitters (each within +-120s of the previous) could
  otherwise ratchet the effective boundary past the tolerance one step at a time (#19). The LATEST
  observed deadline is still what becomes the emitted cycle's own `reset_utc`.
- **Backfill/live dedup uses the same +-120s window-identity rule**, not exact `reset_utc`
  equality -- a live-written row and a backfill-derived candidate for the same real window can
  legitimately differ by a few seconds of replay jitter; backfill never overwrites an existing
  match, so a live row (carrying calibration data backfill can never supply) always wins by
  construction (#20).

### Period presets select CLOSED cycles (#18)

`StatisticsDataLoader.Load`'s period-preset filter now selects cycles by `reset_utc > cutoff`, not
`started_utc >= cutoff` -- a weekly cycle that started five days before a preset's cutoff and
closed two days after it is a genuinely completed week inside "the last N days", and the old
filter dropped it entirely. (`Recent cycles` is unaffected -- it was, and remains, its own
always-on `started_utc`-based ~14-day view, independent of the period preset.)

### `final_pct` is the final envelope value (#15)

Both the live tracker and backfill now record `final_pct` as the ENVELOPE's own value at close
(identical to `peak_pct`, since the envelope only rises) -- never the raw last-accepted reading,
which used to let a replica regression just before rollover (..., 100, 95) report `final_pct=95`
despite the feature's whole premise being that usage never truly decreases.

### Statistics window: duplicate-aware account selector

The statistics window's account selector (`StatusBarApplicationContext.BuildStatisticsForm`) now
filters out `AccountRuntime.IsDuplicate` accounts, the same "keep only the first" rule the tray and
panel already apply (`Model/AccountDuplicates.cs`) -- a duplicate login no longer gets a second,
identical entry in the selector.

### Deviations from the review's own "smallest fix" (the decisions table overrides these)

- **#19's canonical-key fix is applied to `StatisticsBackfill.GroupWindow` only**, not to
  `WindowTracker`'s live rollover-jitter classification (`ClassifyWindowKey`), even though the
  review's underlying mechanism is structurally similar there. `WindowTracker`'s jitter handling is
  a separate, heavily-tested, delicate subsystem (`docs/forecast-and-states.md`'s own decision
  history) outside this round's stated scope, and the review's own concrete example is phrased
  against backfill specifically. Flagged here as a known follow-up, not fixed in this pass.
- **#11 ("every compared bucket needs a real observation opportunity")** was implemented, in this
  round, as "at least two distinct buckets are represented, and every one that is must clear
  `MinObservations`", rather than literally "all 24 hours / all 7 weekdays must be represented".
  That "≥2 buckets" bar turned out to be too weak on its own -- a narrowly-focused usage pattern
  (e.g. the app only ever running 14:00-16:00) could satisfy it while 21+ hours were never
  observed at all, and was replaced 2026-09-21 by a 12-of-24 / 5-of-7 QUORUM rule. See "The
  12-of-24 / 5-of-7 quorum" above for the current rule and why it still stops short of requiring
  literal full-day/full-week coverage (same "prefer the learning state, not an unreachable one"
  reasoning as before, just recalibrated against a stronger quorum).

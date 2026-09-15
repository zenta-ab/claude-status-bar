# Forecast, states and panel — v1 spec

Decision record for milestones M3–M7 (forecast, state machine, freshness, exhausted
state, complete panel). Code contract: `src/ClaudeStatusBar/Model/QuotaContracts.cs`.

## Evidence this rests on

- **Window shape.** 85 polls over 44 min (2026-09-11): `five_hour.resets_at` stayed on
  exactly `11:20:00` UTC to the second while usage rose 12 % → 19 %. A rolling window
  would have slid ~44 min in that time. **Confirmed by the first observed rollover**
  (2026-09-11 11:20 UTC): at the reset instant usage went 60 % → 0 % and `resets_at` jumped
  by exactly 5 h to `16:20:00`. It is a **fixed tumbling window**.
- **Replicas straddle a rollover.** For several minutes after that reset, replies alternated
  between the new window (0 %, 16:20) and the old one (60 %, 11:20, a previously seen
  fingerprint). Once a newer window is accepted, older windows are ignored, never rolled back
  to. Regression fixture: `tests/ClaudeStatusBar.Tests/fixtures/rollover-2026-09-11.csv`.
- **Staleness.** `get_usage` refreshes about every 5 min. Between refreshes it returns
  byte-identical `resets_at` strings. The microsecond digits are the snapshot fingerprint.
- **Replica skew.** Usage regresses within a window (15→14, 19→18, 37→34), sometimes with
  a previously seen fingerprint coming back. Real consumption never decreases inside a
  tumbling window, so every drop is noise.
- **Staircase jumps.** After a frozen stretch, usage can jump 11 points in one step.

## Ingest (per window; session W = 300 min, weekly W = 10 080 min)

1. **Window key** = the tracked deadline, jitter-tolerant. A deadline within **±120 s** of the
   current one is the *same* window (wire-format jitter on the fractional seconds); the latest
   raw string observed is kept as the deadline, but nothing is cleared. A genuinely later
   deadline (>120 s ahead) is a rollover only if it is also *plausible*: `now` must already be
   within 120 s of the **old** deadline (the window was actually due to end). An implausible
   later deadline is ignored, unless it recurs 3 **consecutive** times, in which case it is
   accepted anyway (safety valve) — consecutive means uninterrupted by any other outcome for the
   *same tracker*: a reply belonging to the current window (even one deduped as an
   already-seen fingerprint) or a regressed-key reply both reset the pending count, so
   non-consecutive candidates spread across ordinary replica traffic can never quietly
   accumulate to 3 (round-2 finding 1). A deadline that regressed by more than 120 s is replica
   noise on the key itself, never a rollover — ignored unconditionally. A drop in usage alone is
   **never** a rollover (−3 points is replica skew). A rollover itself is validated against the
   same ordering check as any other sample (below), not exempted from it — an accepted candidate
   that is chronologically *older* than the tracker's last accepted sample is rejected even if it
   otherwise looks like a plausible rollover. `resets_at` is also sanity-bounded to
   `[now − 1 d, now + 8 d]` at both ingest and replay; anything outside that (a garbled or
   adversarial value, e.g. a year-9999 or year-1 timestamp, which still *parses*) is rejected
   outright rather than accepted and later crashing derived-deadline arithmetic.
2. **Fingerprint** = the raw `resets_at` string. A sample whose fingerprint was already seen
   in this window is skipped (stale cache or replica revert).
3. **Monotone envelope**: `P = max(P, sample)` within a window. Drops are ignored.
4. **Time**: the estimator's timeline is UTC. For live samples, cross-check the UTC delta
   against `Environment.TickCount64`; if they disagree by more than 15 s (sleep, clock jump),
   trust the monotonic delta — unless the monotonic delta itself is *negative*, which cannot
   happen within one continuous live process (only an actual reboot resets `TickCount64`, which
   also means a fresh, Cold tracker in practice, never a same-instance transition); when that
   happens anyway, mono is simply not comparable across the gap and the UTC delta is trusted
   instead. `dt ≤ 0` → drop the sample. Every OTHER live duration the model computes — freshness
   deadlines, hysteresis persistence/cooldown, "burning" classification, idle (below), and
   since-rollover — is likewise monotonic, not UTC, and is re-anchored/re-frozen only when a
   sample is *accepted*, never on a poll that merely round-trips a cached duplicate (round-2
   decision 2). A duplicate reply must never quietly reset the clock-jump baseline or extend a
   freshness deadline on data that is not actually new.
5. **Elapsed** `E = W − (resets_at − now)`, recomputed on every use, never accumulated.
6. **Warm start**: after the first successful poll identifies the current window keys, replay
   the rows of `window-shape.csv` belonging to those keys (last `W` only) through the same
   ingest, using `utc_iso` as time. Open the file with `FileShare.ReadWrite`; skip malformed rows.

## Estimator: ratio-EWMA

Never smooth the per-poll derivative (with 1-point quantization it is a square wave).
Keep two decayed accumulators and divide them:

```
d  = exp(−dt / τ)
Sp = Sp·d + dP        // decayed points consumed
St = St·d + dt        // decayed minutes
r_ewma = Sp / St      // %/min
```

- Session: half-life 15 min → τ = 21.64 min. Weekly: half-life 8 h → τ = 692.5 min.
- **Seed** (cold start, and after a rollover): window-to-date pace `r_wtd = P / E`.
  `m0 = min(E, τ)`; `Sp = r_wtd·m0`, `St = m0`. Valid only when `E ≥ W/30` and `P ≥ 3`.
- **Point rate**
  - Session: `r = max(r_ewma, floor)`, where `floor = r_wtd · exp(−idle / 30 min)` and
    `idle` = time between the last accepted *rise* and the *latest accepted observation*
    (confirmed by novel fingerprints reporting no change, not by mere wall-clock passage with no
    polling at all) — **frozen at that latest accept, in monotonic minutes, never recomputed
    against the live evaluation clock** (round-2 finding 7). Measuring it through `now` instead
    let the floor keep relaxing every second even with no new poll at all, silently clearing the
    protection during an ordinary gap between polls rather than only after a genuinely confirmed
    stop. The window-to-date pace is a floor, never forecast slower — but a floor that never
    decayed kept a window "red" for hours after consumption had genuinely stopped; decaying it
    with confirmed idle time lets a real stop clear the verdict while a short reading pause still
    keeps the protection.
  - Weekly: `w = min(0.5, St/τ)`; `r = (1−w)·r_wtd + w·r_ewma`. It is dominated by
    window-to-date pace, and the day-of-week profile is off.
- **Rollover**: hard-clear accumulators, fingerprints and envelope, then re-seed.

## Forecast

```
rem       = 100 − P
t_reset   = minutes until resets_at
t_dep     = rem / r            (∞ when r ≈ 0)
shortfall = max(0, t_reset − t_dep)   // minutes blocked before the reset
slack     = t_dep − t_reset           // negative = you run dry first
projected = P + r·t_reset             // uncapped; wedge draws min(projected,100) − P
pace      = r / (100 / W)             // 1.0 = exactly linear
```

Worked example (session): P = 56, E = 132, t_reset = 168, r = 0.424 → t_dep = 104,
shortfall = 64 min → **DryEarly**.

`Shortfall` (the contract's `WindowView.Shortfall`) is kept non-nullable for contract stability
(round-2 decision 11, "partial" — a full nullable `TimeSpan?` would have been a public contract
change, out of scope for that round). While `State` is Measuring, `Shortfall` is a fixed
`TimeSpan.Zero` placeholder that carries **no meaning at all** — not "confirmed zero blockage" —
exactly like every other forecast field. Every renderer already gates display on `State` and must
never read `Shortfall` without checking it first.

## Refusal → `Measuring`

Show % and the countdown, but no colour verdict and no wedge, when **any** of these hold:
no valid seed and fewer than 2 ingested samples · `E < W/30` for session (10 min) but
**`E < 24 h` (1440 min) for weekly**, not `W/30` (5.6 h) · `P < 3` · less than 5 min since a
rollover · `resets_at` has passed with no later window
observed yet ("awaiting reset") · this window was missing or invalid in the latest successful
poll ("data saknas", decision 13 — a window's validity is tracked per-poll; a successful poll
overall does not mean every window's data in it was usable, so a missing weekly node is never
silently treated as 0 %) · a clock discontinuity was detected (decision 6). Silence beats a
wrong verdict. Whenever the final state is Measuring for **any** reason, every forecast display
field (rate, pace, projected %, depletion time, shortfall) is null — never just withheld for
some Measuring reasons and not others (decision 12).

The weekly `E < 24 h` floor (`MeasuringReasonCode.TooEarlyInWeek`, "För tidigt i veckan — väntar
på ett helt dygn") exists because a pace read on day one of the week has not yet lived through a
full day/night cycle: a Friday-morning-only working pace extrapolated straight through the
coming nights and weekend used to fire a false DryEarly alarm at ~11 % used, ~340 min after a
07:00 reset (just past the old `W/30` = 336 min line). The weekly `τ` (half-life) must span a
full day/night cycle or the pace it reports excludes the nights the 7-day budget actually
contains — this also applies to the seed itself (`Ingest`'s seed check uses the same 24h
minimum), not just the refusal read. Session is unaffected; hour-of-day/weekday pattern
learning is tracked separately as a backlog item, not attempted here.

## States (per window)

`S_lo = max(0.05·W, 10)`, `S_hi = max(0.08·W, 15)` → session 15 / 24 min, weekly 504 / 806 min.

| Order | Condition | State |
|---|---|---|
| 1 | `P ≥ 100` | **Spent** (immediate, exempt from hysteresis) |
| 2 | refusal rule | **Measuring** |
| 3 | `shortfall ≥ S_lo` or `rem ≤ 3` | **DryEarly** |
| 4 | `shortfall > 0` or `slack ≤ S_hi` or `rem ≤ 10` | **Tight** |
| 5 | otherwise | **Safe** |

Spent requires *confirmed* exhaustion (`P ≥ 100`), not "almost" (the old 99.5 threshold claimed
blocking without evidence of blocking). 97–99.9 % still reaches DryEarly on its own via the
`rem ≤ 3` clause above, so nothing in the 97–100 % range goes unflagged.

**Grace** (applied after the table, immediately, not through hysteresis): `t_reset ≤ 15 min`
caps the state at **Tight** — never at Safe. Grace must never turn a predicted cutoff into Safe:
if the model's own forecast says depletion precedes the reset, the worst grace may do is quiet
DryEarly down to Tight, not erase the warning. Spent is never capped, and a cap can only ever
lower severity, never raise it.
The alarm is never a function of `P` alone. At 90 % with 5 min left and no further consumption,
`rem = 10 ≤ 10` already reads Tight from the raw table itself — grace has nothing left to lower
it to Safe even if it still could. At 60 % burning 0.6 %/min with 4 h left, the shortfall is
~173 min, so that's DryEarly.

**Hysteresis** (on *accepted* observations, never on a poll tick, a duplicate/deduped sample, or
a transport failure):

- **Escalate**: commits to the *lowest* severity that is still above Committed, seen across the
  last 2 accepted observations (weekly: 4) — not exact repeated agreement on one target state.
  Two different-but-both-escalating raw states (e.g. Tight then DryEarly) count together as
  evidence; an exact-match rule could otherwise stall on the old state indefinitely while raw
  alternates between two higher ones.
- **De-escalate**: needs 3 accepted observations agreeing on the same lower state (weekly: 4)
  **and** at least 10 real minutes since the first of them (weekly: 2 h) — replacing plain poll
  counts, which scaled with polling policy instead of wall-clock confidence.
- At most one committed change per 90 s (weekly: 5 min) either direction.
- A transport failure neither counts as evidence nor resets pending evidence: it simply never
  steps hysteresis at all, since no new (or any) observation was actually received.
- Spent, grace and rollover bypass hysteresis. **Leaving Measuring also commits
  immediately, in either direction**: Measuring is not a verdict, so there is
  nothing to debounce against. Hysteresis only ever arbitrates between the
  three verdict states (Safe/Tight/DryEarly) once already in one of them.

**Icon severity** = max(session, weekly) of the committed states. Measuring ranks lowest.

## Freshness ladder

Evaluated on the always-running 1 s UI timer, so a hung poll can never freeze a confident
number on screen. The deadlines below are **frozen**: each successful poll computes
`stale_at = t + 3·c_policy` and `unknown_at = t + 10·c_policy` once, where `c_policy` is the
plain burning/idle/spent policy interval **without** the soonest-reset cap and **without**
failure backoff. Neither a later failure nor the mere passage of time can move these deadlines
later — only a *newer* successful poll can, which is the only thing allowed to improve
freshness. (An earlier design recomputed `3c`/`10c` live against the currently-scheduled poll
interval; a shrinking reset-cap interval then made freshness collapse right before a normally
scheduled poll even landed, and a growing failure-backoff interval let it falsely claim Live —
or even "improve" from Unknown back to Stale — through a prolonged outage.)

- **Unknown**: no successful poll yet, or `now` is past the frozen `unknown_at`. The icon shows
  a grey outline with no arcs, and the panel says "Kan inte läsa kvoten".
- **Stale**: `now` is past the frozen `stale_at`; the session fingerprint is unchanged for more
  than 20 min; `now > resets_at + 60 s` for any window (reset passed, new data not in yet); or a
  clock discontinuity was detected (see Evaluate, below). The icon drops to 70 % opacity with a
  dot, and the panel shows a warning with the real age.
- **Live**: otherwise.

**Clock discontinuity.** QuotaModel keeps a UTC↔monotonic anchor `(anchorUtc, anchorMono)` from
the last accepted *live* sample (never touched by CSV replay, which is UTC-only — see Ingest
rule 6). On every `Evaluate(utcNow, monoMs)`, `expected = anchorUtc + (monoMs − anchorMono)`; if
`|utcNow − expected| > 60 s`, the clock jumped (sleep, manual change, VM pause) independently of
whether it went forward or backward. While that holds: freshness is forced Stale (real data
exists, just an untrustworthy clock) and every window's state is forced Measuring — bypassing
grace entirely, so a reset that only *appears* close because of the jump can never be read as
Safe — until the next accepted live sample re-anchors.

**Sleep across a reset.** If the machine sleeps across a window's `resets_at` and the resumed
poll's response still describes the *old* window (a cached reply, deduped by fingerprint), the
window does not keep presenting an expired forecast as current: once `resets_at` has passed
with no later window observed yet, that window reads **Measuring** ("Nytt fönster väntas"), its
countdown shows "Återställs nu…", and freshness is Stale via the `resets_at + 60 s` rule above.

## Exhausted (any window Spent)

- `BlockedUntil` = the latest `resets_at` among spent windows.
- The whole icon dims (brightness 0.85 on a dark taskbar, 0.42 on light), the ring becomes a dashed outline,
  and the inner pie becomes a countdown: `(BlockedUntil − now) / W` of the blocking window,
  drawn in `#9AA3AE`. It must measure **≥ 5:1 contrast at 16 px on `#202020`**. The earlier
  `#6C7480` at 42 % measured 1.71:1 and was invisible.
- The icon re-renders at most every 15 s in this state (the pie changes slowly).

## Poll cadence

| Condition | Interval |
|---|---|
| a `resets_at` has passed with no later window observed yet ("awaiting reset") | 30 s |
| any window Spent (and not also awaiting reset) | 300 s |
| usage rose within the last 10 min ("burning") | 30 s |
| otherwise ("idle") | 150 s |
| after a failure | 60 → 120 → 240 → 480 → 600 s cap |

"Awaiting reset" is checked **before** Spent: an *expired* Spent window (its own `resets_at` has
already passed) schedules at the fast 30 s cadence, not the slow 300 s one — the 300 s interval
is reserved for a window that is Spent **and genuinely still current**. Checking Spent first used
to pin recovery polling at 300 s for up to five minutes after a reset that would have cleared the
block (round-2 finding 8).

A deadline still ahead of `now` caps the interval down to that `resets_at + 3 s`, so one poll
lands right on it — but only once: the instant it passes, that same deadline stops capping
anything (that is what the "awaiting reset" row above is for). Capping down to an
**already-past** deadline forever is exactly what used to turn into an endless 1 s poll loop
after a reset whose new window hadn't shown up in cached data yet; a past deadline also never
overrides a failure's backoff interval.

## Panel (complete)

340 px wide, height fitted to content. Top to bottom:

1. **Header**: "Claude Code" and the freshness line ("Uppdaterad för X min sedan", warning
   style when Stale).
2. **Verdict banner**, shown only when severity ≥ Tight, Spent, Stale or Unknown. One sentence, e.g.
   "Sessionen tar slut kl 12:36 — 44 min före reset" or "Kvoten slut · öppnar igen kl 13:20 (om 1 h 41 min)".
3. **Two cards** (session, weekly). Each has a ring with the used arc in its state colour plus a forecast
   wedge to `projected`, drawn by the same geometry as the icon. Below it: large % used,
   "Återställs om …", "kl …", and a forecast line: "Takt 1,3× jämn · slut kl 12:36" /
   "Takt 0,8× jämn · räcker till reset" / "Mäter takt… (reason)".
4. **Session timeline**, full width: window start → reset, elapsed shaded, a "nu" marker, and
   the projected depletion marker. When depletion lands before the reset, the depletion→reset span is
   hatched in the critical colour and labelled "stillastående ~44 min". HH:mm labels.
   A compact weekly timeline goes below it, with day labels.
5. **Footer**: last poll time and the current poll interval.

## Colours

Safe `#12A277` · Tight `#E8A020` · DryEarly `#E23D28` · Spent `#9AA3AE` · Measuring: a neutral
grey distinct from Spent.

## Implementation notes (M3-M7): ambiguities resolved while building the model

This spec left a handful of points underdetermined. Each was resolved toward
whichever reading fails safer (silence/caution over a wrong verdict), per the
project's own principle above. These notes predate the review round below;
where a decision in that section conflicts with one of them, the decision
wins (each such case is cross-referenced inline).

- **"No valid seed" in the refusal rule** (`Model/WindowTracker.cs`). Read
  literally, `no valid seed and fewer than 2 ingested samples` is a compound
  AND, and "valid seed" is defined by the exact same thresholds as the two
  clauses next to it (`E ≥ W/30`, `P ≥ 3`). So whenever those two hold, a seed
  is always computable from the very first sample -- the "fewer than 2
  samples" half can never independently block by itself. This is intentional,
  not a bug: it's what makes the worked example (a single first sample)
  produce a verdict immediately rather than waiting for a second poll.
  Implemented literally as written; confirmed against the worked-example test.
  Superseded in one respect by decision 12 below: seed eligibility for this
  same clause is now *re-checked live* in `ComputeSnapshot`, from `E` and `P`
  as of `utcNow`, rather than solely from the stored "was a seed ever
  computed" flag -- so time alone (no new sample) can newly satisfy it, which
  matters when every poll since arrival happened to be a cached duplicate.

- **Grace's timing relative to hysteresis.** "Applied after the table,
  immediately, not through hysteresis" is implemented as a fresh clamp on
  every `Evaluate()` (the 1 s tick), computed from the live time-to-reset, on
  top of whatever hysteresis last committed at the last poll. It is not itself
  debounced and does not persist as committed state -- if `t_reset` drifts
  back out past the 15 min line before the next poll, the clamp simply stops
  applying on the next tick. Grace can only ever lower severity, never raise
  it (Measuring is unaffected, since its severity is already the lowest). The
  second tier (`t_reset ≤ 5 min` capping to Safe) was removed by decision 2:
  it could turn a predicted cutoff into a false "Safe" right before the reset
  that was supposed to relieve it.

- **Which fingerprint drives `QuotaView.LastChangedAt` and the 20-minute
  "Stale" fingerprint check.** The doc says "fingerprint unchanged" without
  saying which window. The pre-existing `Model/PanelSnapshot.cs` already
  tracked only the *session* window's `resets_at` string for exactly this
  purpose ("the only honest staleness signal available"), and the real log
  shows session and weekly refresh together in one `get_usage` response. Kept
  that precedent: both fields are driven by the session window's
  last-fingerprint-change time only.

- **Warm-start ordering** (`QuotaModel.WarmStart`). The doc's "after the first
  successful poll identifies the current window keys, replay..." reads as
  "replay after ingesting the live sample," but doing it in that order feeds
  the estimator's dt-guard (`dt ≤ 0` → drop) a set of rows that are all
  *older* than the live sample it just accumulated, so every replayed row
  would be silently dropped and warm start would do nothing. `WarmStart` must
  therefore run **before** the first `Ingest()` call for that snapshot, using
  the snapshot's own `resets_at` fields (not tracker state) to identify the
  target window keys. See the method's doc comment; the UI builder must call
  it in that order (below). Decision 14 refines *when* WarmStart is still
  eligible: gated on whether a live sample has ever been **successfully**
  ingested, not merely on whether any poll (success or failure) has completed
  -- an initial transport failure must not permanently disable it.
  Decision 7 adds that replay itself is UTC-only (it never persists a
  monotonic baseline), so the live monotonic epoch after replay is always
  established fresh at the first live sample, never mixed with a CSV row's
  (possibly previous-boot) `mono_ms`.

- **`E < W/30` vs. a negative/degenerate `E`.** Not addressed by the doc
  directly. `r_wtd` guards `E ≤ 0` to avoid a division by a non-positive
  number (returns 0 rather than NaN/Infinity); this only matters in
  pathological warm-start replay of very old rows and never affects the
  refusal rule itself, which already gates on `E < W/30` first.

- **`NextPollDelay` floor.** Capping to `resets_at + 3s` can produce a
  same-instant or already-past target. Clamped to a 1 s minimum so the result
  is always a valid `System.Windows.Forms.Timer.Interval` (which must be
  `> 0`), rather than 0 or negative.

## Review round 1 — decisions (Codex, 2026-09-11)

Full review: `docs/reviews/2026-09-11-codex-quota-model.md`. Every finding was evaluated. The
decisions below override anything above that conflicts with them.

| # | Finding | Decision |
|---|---|---|
| 1 | One-second `resets_at` jitter splits a window (real: 19:10:00 → 19:09:59 on 2026-09-09) | **Accept.** A deadline within ±120 s of the current one is the same window; keep the latest raw string as the deadline. Rollover requires new deadline > current + 120 s **and** `now ≥ current deadline − 120 s`. An implausible later deadline is ignored; if it persists for 3 consecutive accepted polls, it is accepted as a rollover (safety valve). |
| 2 | Grace turns a predicted cutoff into Safe | **Accept.** Grace never produces Safe while `t_dep < t_reset`. `t_reset ≤ 15 min` caps DryEarly at Tight (no red right before a reset), and the ≤ 5 min → Safe rule is removed. |
| 3, 5 | Freshness multiplier `c` shrinks with the reset cap and grows with backoff | **Accept.** Each successful poll freezes `stale_at = t + 3·c_policy` and `unknown_at = t + 10·c_policy`, with `c_policy ∈ {30, 150, 300} s` (no reset cap, no backoff). Failures and elapsed time can never improve freshness. The 20-min fingerprint rule is unchanged. |
| 4 | Endless 1 s polling after a reset while cached data still shows the old window | **Accept.** Each deadline is serviced by one poll at `reset + 3 s`. After that, until a new window is observed, poll every 30 s ("awaiting reset"), subject to failure backoff. A past deadline never caps the delay again. |
| 6 | Clock jumps affect verdicts and freshness; `Evaluate` ignores `monoMs` | **Accept.** All durations (freshness ages, cooldowns, burning, since-rollover) use monotonic time. Keep a UTC↔mono anchor from the last accepted live sample. If `|utcNow − (anchorUtc + Δmono)| > 60 s`, it's a clock discontinuity: freshness Stale, verdicts Measuring ("klockan ändrades") until the next accepted sample re-anchors. |
| 7 | Replay mixes monotonic epochs across reboots; rejected samples still mutate state | **Accept.** Replay is UTC-only. The live monotonic baseline starts fresh at the first live sample. A sample mutates tracker state only after passing every timing/validity check (atomic accept). |
| 8 | CSV rows from the future override live data | **Accept.** Replay rejects rows with `utc > now + 60 s` and rows before the window start (`resets_at − W`). |
| 9 | NaN/out-of-range values poison the model | **Accept.** Utilization must be finite and in [0, 100] at both the CSV and tracker boundaries. Derived timestamps are guarded. |
| 10 | The WTD floor keeps a window red for ~2 h after consumption stops | **Accept, modified.** The session floor decays with confirmed idle time: `floor = r_wtd · exp(−idle / 30 min)`, where idle = time since the last accepted rise, confirmed by novel fingerprints without a rise. Short reading pauses keep the protection; genuine stops end the red. |
| 11 | Hysteresis counts duplicate replies and poll ticks as evidence | **Accept.** Hysteresis advances only on accepted (novel) observations. Escalation commits to the lowest level ≥ current seen in the last 2 accepted observations. De-escalation needs 3 accepted observations and ≥ 10 min (weekly: 4 and ≥ 2 h). Failures neither count nor reset pending evidence. |
| 12 | Measuring leaks forecast fields; duplicates never seed | **Accept.** All forecast fields are null whenever State is Measuring. Seed eligibility is re-checked in `Evaluate`, so time alone can make the seed valid. |
| 13 | A successful poll isn't necessarily usable data | **Accept.** Validity is tracked per window. A window missing or invalid in the latest successful poll becomes Measuring ("data saknas") and drops its forecast. Global Live requires a valid session window. |
| 14 | WarmStart is skipped after an initial failure | **Accept.** WarmStart is allowed until the first successful ingest. |
| 15 | Spent at 99.5 % claims blocking without evidence | **Accept.** Spent only at `P ≥ 100`. 97–99.9 % is DryEarly via `rem ≤ 3`. |
| 16 | Forecasts presented as certainty | **Accept (copy only).** All forecast text is conditional ("i nuvarande takt"). |
| — | Sleep across a reset, with the resumed response still showing the old window | **Add.** Explicit awaiting-reset condition: the window is Measuring ("Nytt fönster väntas"), the countdown shows "Återställs nu…", and freshness is Stale. |
| — | Weekly false alarm on day 1 → weekly refusal until E ≥ 24 h | **Add.** A weekly pace read before a full day/night cycle has elapsed extrapolates straight through the coming nights and weekend (real case: 11 % used, E ≈ 342 min after a 07:00 reset — just past the old `W/30` = 336 min line — fired a false DryEarly). The weekly window's refusal rule and seed validity both use `E ≥ 1440 min` (24 h) in place of `W/30`; session is unchanged. Reason: "För tidigt i veckan — väntar på ett helt dygn". Hour-of-day/weekday pattern learning is out of scope, tracked as a backlog item. |

Test gaps accepted from the review: findings 1–9 as end-to-end traces with `Evaluate` calls between
polls; golden replay over the **full** fixture, including the 2026-09-09 jitter rows; EWMA verified
independently of the WTD floor; `NextPollDelay` asserted positive, deadline-exact and correct after
the deadline; backoff tested with reset data present; WarmStart integration (initial failure, future
rows, reboot, equal timestamps); sleep across a reset.

## Review round 2 — decisions (Codex, 2026-09-11)

Full review: `docs/reviews/2026-09-11-codex-quota-model-round2.md`. No public contract change
(`IQuotaModel`, `QuotaView`, `WindowView`) in this round — the UI/runtime layers were hardened in
parallel and the contract is their stable seam. The decisions below override anything above that
conflicts with them; sections earlier in this document were also edited in place where the body
had gone stale against the implementation (monotonic durations, candidate continuity, idle
measured through the last accepted observation, the expired-Spent poll schedule, the `resets_at`
sanity bounds, and `Shortfall`'s meaninglessness in Measuring).

| # | Finding | Decision |
|---|---|---|
| 1 | Non-consecutive replica replies trigger the rollover safety valve; rollover skips ordering checks | **Accept.** Candidate bookkeeping stays separate from accepted state. Continuity resets on any intervening non-matching outcome (a same-window reply, even one that ends up fingerprint-deduped; a regressed-window reply; an invalid sample). A rollover passes the same ordering check as any sample, read against the tracker's last-accepted timing baseline *before* that baseline is cleared for the new window. |
| 2 | A cached reply re-anchors the clock guard; durations still in UTC | **Accept, no contract change.** Re-anchor (and re-freeze freshness deadlines) only when a live observation is ACCEPTED. Freshness deadlines, hysteresis persistence/cooldown, "burning", idle and since-rollover durations are monotonic. `NextPollDelay(utcNow)` uses the latest `monoMs` the model received via `Ingest`/`Evaluate` (not `IngestFailure`, read literally). |
| 3 | Invalid session utilization keeps a verdict and Live | **Accept.** Session validity requires `IsValidPct`, exactly like weekly. |
| 4 | Measuring gets stuck after time-only eligibility, and after a replay→live duplicate (common after every restart) | **Accept.** When the committed state is Measuring and the current snapshot is no longer refused, the verdict commits immediately on the next `Evaluate` tick, without hysteresis evidence — covering the rollover/weekly-24h gates lifting with time, and a WarmStart-seeded window whose only live poll was deduped. |
| 5 | A CSV row up to 60 s in the future still overrides live data | **Accept.** Replay excludes every row later than the first live observation's UTC (no tolerance). |
| 6 | An unusable response consumes WarmStart; replay membership ignores jitter | **Accept.** Only a usable, ACCEPTED live ingest consumes WarmStart eligibility. Replay membership uses the same ±120 s jitter rule as live ingest. |
| 7 | "Confirmed idle" keeps decaying during silence | **Accept.** Idle is measured through the last accepted unchanged observation, frozen in monotonic minutes at that accept — never through `now`. No decay without evidence. |
| 8 | An expired Spent hold keeps 300 s polling | **Accept.** Awaiting-reset is checked before Spent; an expired Spent window schedules at 30 s. |
| 9 | Demo states violate model invariants | **Accept.** The weekly Safe demo gets E ≥ 1440 min; the awaiting-reset demo shows Stale freshness. |
| 10 | Extreme timestamps throw | **Accept.** `resets_at` is rejected outside `[now − 1 d, now + 8 d]` at both ingest (`WindowTracker.Ingest`) and replay (`CsvReplay.FilterForWindow`, `QuotaModel.ReplayWindow`); every derived deadline addition is also defensively guarded (`QuotaTimeUtil.SafeAdd`). |
| 11 | `Shortfall` is non-nullable during Measuring | **Partial, unchanged from the review's own recommendation.** Kept non-nullable for contract stability; `TimeSpan.Zero` during Measuring is documented as meaningless, not "confirmed zero". |
| — | Test critiques | **Accept all**, implemented as reasonably as the existing suite's structure allowed; see "Deviations" below for the handful of test-critique items given a lighter touch than the review's own description implies. |

### Deviations from the review's literal text, and why

- **Finding 1's "accepted current-window sample" reset condition, read broadly.** The review's
  smallest fix says continuity resets on "an accepted current-window sample". The failing trace it
  gives, though, alternates the current-window reply with the candidate — and the current-window
  replies in that trace are *fingerprint duplicates*, which never reach "accepted" in the
  mutation sense. Implemented continuity-reset on *any* same-window classification (`ClassifyWindowKey`
  returning `SameWindow`), whether or not the sample goes on to be deduped — otherwise the review's
  own reproduction case would not be fixed. Regression test:
  `SafetyValve_NonConsecutiveCandidateReplies_NeverAccumulate_AcrossInterveningCurrentWindowReplies`.
- **Finding 2's monotonic "LastChangedAt" driving the 20-minute fingerprint-stale freshness
  rule.** Rather than adding a second per-window monotonic-accept field, `FreshnessOracle` now
  reuses `QuotaModel`'s own UTC↔mono anchor (`_anchorMono`) for this check — since decision 2
  already restricts that anchor to "the last accepted live observation", it already carries
  exactly the signal the 20-minute rule needs, without a redundant parallel field. The
  round-1-era precedent that this is *specifically the session window's* fingerprint no longer
  applies bit-for-bit (the anchor updates on either window's acceptance), but round 1's own
  justification — "session and weekly refresh together in one response, so the two move in
  lockstep in practice" — already covers the difference. `QuotaView.LastChangedAt` (the display
  field) is untouched and still session-only, UTC, exactly as before.
- **Finding 7's idle floor when no live rise has ever happened.** Not addressed explicitly by the
  review. Implemented as: idle stays `0` (the maximally protective value, i.e. no decay at all)
  until a *live* (non-replay) accepted sample establishes a rise baseline — matching "no decay
  without evidence" rather than falling back to a UTC-based idle for that narrow window.
- **Reboot-spanning mono deltas in `WindowTracker.ComputeDtMinutes`.** Not a review finding, but
  a genuine edge case the ordering-check fix (decision 1) surfaced: replaying two real days' raw
  log data that happen to straddle an actual machine reboot through one *live*-mode tracker
  produces a negative monotonic delta (`TickCount64` resets near 0 across a reboot). A negative
  monotonic delta cannot occur within one continuous process, so it is now treated as "mono not
  comparable here" and the UTC delta is trusted instead, rather than concluding the sample is out
  of order. `GoldenReplayTests.FullFixture_ReplayedContinuously_WithoutPreFiltering_...`
  (pre-existing) is what caught this.
- **Test critiques given a lighter touch.** All were addressed, but two at less than the review's
  full ask given the size of this round: the WarmStart-tests-distinguishable-from-cold-start
  critique is satisfied via a CSV row with a *higher* percentage than the live sample (so only an
  actual replay could produce the observed envelope) rather than an independent `SampleCount`
  assertion path through the public contract (which does not expose `SampleCount`); the golden-
  replay critique is satisfied with a per-observation monotone-envelope invariant rather than a
  fully independent recomputation of the accumulator/forecast checkpoints.

### Verification note (task item #4)

After these fixes, a live restart against the real `window-shape-*.csv` history shows the session
window committing a real verdict (not Measuring) at the very first successful poll, with
`Freshness=Live` — confirmed via `committed-state.log` (a one-line-per-poll diagnostic added to
`StatusBarApplicationContext.ApplyResult` for this verification). The weekly window correctly
stays Measuring ("För tidigt i veckan…") since it was under 24 h into its window at verification
time — expected, not a defect.

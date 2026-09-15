The strongest failures are **false rollovers from one-second jitter, freshness thresholds collapsing near reset, endless one-second polling after reset, and replay corrupting the estimator across clock/boot boundaries**. Separately, the spec deliberately calls some predicted cutoffs “Safe.”

This is a static review of the inline text; no commands or files were accessed. Examples below use UTC on **2026-01-01** unless specified. Define these exact raw strings:

- `A = "2026-01-01T05:00:00.000000+00:00"`
- `B = "2026-01-01T05:00:00.100000+00:00"`
- `C = "2026-01-01T05:00:00.200000+00:00"`

Triples are `(utcNow, pct, raw)`. Unless stated otherwise, monotonic time advances with UTC and weekly data is absent. User-visible outcomes refer to the returned model view; renderer and poll-loop implementations were not supplied.

**High — 1. One-second jitter loses real consumption or fabricates a rollover**

Locations: `WindowTracker.cs:71–79`; `docs/forecast-and-states.md:22–23`.

**Already present in the supplied fixture:**

```text
(2026-09-09T15:31:45.2179971Z, 85, "2026-09-09T19:10:00.218248+00:00")
(2026-09-09T15:34:33.8679802Z, 94, "2026-09-09T19:09:59.450402+00:00")
```

The second sample is discarded entirely because its truncated reset is one second earlier. The model retains **85% while the server reports 94%**.

The opposite direction is destructive:

```text
(02:00:00Z, 80, A)
(02:05:00Z, 77, "2026-01-01T05:00:01.100000+00:00")
(02:10:00Z, 90, B)
```

The second sample clears the 80% envelope, seeds 77%, and forces Measuring. The third, correct-second sample is ignored indefinitely. Repeated one-second advances less than five minutes apart can keep the model Measuring continuously.

**Smallest fix:** normalize small reset corrections into the current window without clearing consumption. Treat a materially later deadline as a rollover candidate and check its plausibility against the previous deadline and observation time. Do not equate every positive second difference with a new quota window.

---

**High — 2. Grace explicitly turns a predicted cutoff into Safe**

Locations: `StateMachine.cs:39–48`; `docs/forecast-and-states.md:90–93`.

```text
(04:50:00Z, 80, A)
(04:55:00Z, 99, B)
```

The second sample produces approximately:

```text
r = 0.968 %/min
remaining = 1%
depletion in ≈ 1.03 min
reset in = 5 min
shortfall ≈ 3.97 min
```

Raw classification is DryEarly because remaining quota is ≤3%. Grace returns **Safe**. If consumption continues at that rate, the user is cut off around 04:56, four minutes before reset.

This is a spec failure, not a forecasting error: the model’s own prediction contradicts its verdict.

**Smallest fix:** preserve Tight whenever depletion precedes reset. If short interruptions deserve a quieter icon, reduce notification urgency separately; do not label the outcome Safe.

---

**High — 3. Normal reset scheduling makes fresh data become Stale, then Unknown**

Locations: `QuotaModel.cs:116,134–137,203–219`; `FreshnessOracle.cs:30–36`.

```text
(04:59:40Z, 50, A)
```

The next poll is correctly scheduled for **05:00:03**, 23 seconds later. No poll is overdue.

Yet the UI timer recomputes `c` as the *remaining time to that deadline*:

| Evaluation time | `c` | Last-success age | Result |
|---|---:|---:|---|
| 04:59:58 | 5 s | 18 s | Stale: age > 3c |
| 05:00:01 | 2 s | 21 s | Unknown: age > 10c |

The icon can go grey before the normally scheduled poll even starts.

**Smallest fix:** separate the next scheduling delay from freshness policy. Use stable age limits or a captured expected-poll deadline plus allowance. Never use a shrinking reset countdown as the freshness multiplier.

---

**High — 4. A reset that remains in cached data creates an endless one-second poll loop**

Location: `QuotaModel.cs:210–219`.

```text
(04:59:40Z, 50, A)
(05:00:03Z, 50, A)
(05:00:04Z, 50, A)
...
```

After reset+3s, `untilCap` remains nonpositive. Every `NextPollDelay()` returns **one second**, forever, until a later reset arrives.

Failures do not help: the reset cap overrides the 60–600 second backoff. A permanently missing weekly rollover can sustain the same loop even after session data recovers.

**User sees:** rapidly changing freshness, repeated errors, potentially continuous CLI activity.

**Smallest fix:** consume each reset deadline once. After the targeted attempt, use a bounded reset-recovery retry interval/backoff. Exclude already-serviced past deadlines from ordinary scheduling caps.

The floor prevents invalid timer intervals; it does **not** prevent a hot loop.

---

**High — 5. Failure backoff moves the freshness goalposts, allowing Live during prolonged failure**

Locations: `QuotaModel.cs:185–188`; `FreshnessOracle.cs:30–36`.

Start with:

```text
(02:00:00Z, 20, A)
```

Then failures at:

```text
02:00:30 → c=60s
02:01:30 → c=120s
02:03:30 → c=240s
02:07:30 → c=480s
02:15:30 → c=600s
```

At **02:15:30**, all checks still permit **Live**:

- Last success: 15.5 minutes ago, below `3c = 30 min`.
- Failure streak: 15 minutes, below `3c`.
- Last fingerprint: 15.5 minutes, below 20 minutes.

A hung poll also produces nonmonotonic confidence: after a sole positive sample, age 10 minutes gives Unknown with `c=30s`; just after 10 minutes, burning expires, `c=150s`, and freshness improves to Stale without any new information.

**Smallest fix:** freeze freshness deadlines independently of backoff and burning transitions. Failures or elapsed time alone must never improve freshness.

---

**High — 6. Monotonic protection covers rate updates, but leaves verdicts and freshness exposed to clock jumps**

Locations: `WindowTracker.cs:119–124,149–167`; `QuotaModel.cs:114–116,206–208`; `StateMachine.cs:108`; `FreshnessOracle.cs:33–39`.

```text
(02:00:00Z, 80, A), mono=0
```

One real minute later, move the clock forward and evaluate:

```text
Evaluate(04:56:00Z, mono=60_000)
```

Grace returns **Safe** because reset appears four minutes away, despite almost three hours remaining in real elapsed time. The rate-delta correction cannot help: Evaluate ignores its `monoMs` argument.

For a backward jump:

```text
(02:00:00Z, 20, A), mono=0
Evaluate(01:20:00Z, mono=1_200_000)
```

Twenty real minutes have passed after a one-hour rollback. Last-success and fingerprint ages are negative, so a hung poll still appears **Live**. Negative elapsed time also extends burning, hysteresis cooldowns, and rollover refusal.

Clock oscillation across 04:55 makes a committed DryEarly verdict flap between Tight and Safe on UI ticks.

**Smallest fix:** use monotonic durations for freshness, cooldowns, burning, and rollover grace. Maintain a UTC/monotonic anchor for deadline calculations; on a detected clock discontinuity, suspend confident forecasts until time/deadline consistency is restored.

Sleep itself does not prove a clock discrepancy: when both clocks advance together, the existing delta comparison detects nothing.

---

**High — 7. Replay mixes monotonic clock epochs; rejected rows still consume quota deltas**

Locations: `QuotaModel.cs:98–99`; `WindowTracker.cs:82–110,119–124`; spec `:31–33`.

Same reset key, across a reboot:

```text
CSV:  (02:00:00Z, 10, A), mono=900_000_000
Live: (02:05:00Z, 50, B), mono=300_000
Live: (02:10:00Z, 60, C), mono=600_000
```

UTC deltas are positive. Monotonic deltas relative to the replayed row are hugely negative, so the code selects negative `dt` and drops both rate updates.

However, it already updated:

- Envelope: 10 → 50 → 60.
- Fingerprint set.
- Sample count.
- Last-rise time.

It never advances `LastAcceptedUtc` or the monotonic baseline. Rate updates can remain broken until rollover or until uptime catches up with the previous boot’s uptime.

At 02:10, the model uses approximately the WTD floor `60/130 = 0.462`, rather than capturing the observed 50-point rise in ten minutes.

**Smallest fix:** replay using UTC deltas only, as the spec says. Explicitly establish a fresh live monotonic baseline at the replay/live boundary. Validate timing before consuming envelope deltas or fingerprints.

---

**High — 8. CSV accepts future rows, letting tomorrow’s “history” override today’s live observation**

Locations: `CsvReplay.cs:63–75`; `QuotaModel.cs:88–99`.

Warm start at **02:10**, with first live snapshot:

```text
(02:10:00Z, 20, A)
```

CSV contains:

```text
(02:20:00Z, 99.6, B)
```

The row passes: there is a lower age bound but **no `row.UtcIso <= now` check**. Replay establishes 99.6%; live ingestion cannot lower the envelope.

**User sees:** immediate **Spent**, blocked until 05:00, although the current live reading is 20%.

Future-dated log rows can arise after correcting a previously fast system clock.

**Smallest fix:** reject future rows. Also bound replay to the inferred window start, not merely `now-W`, and define deterministic handling for equal timestamps.

---

**High — 9. Invalid numeric CSV data can poison the model and make Evaluate throw**

Locations: `CsvReplay.cs:105–106`; `WindowTracker.cs:87`; `QuotaModel.cs:157–167`.

Replay this otherwise structurally valid row:

```text
2026-01-01T02:00:00Z,0,NaN,2026-01-01T05:00:00.000000+00:00,,,20,get_usage
```

Floating-point parsing accepts `NaN`; the envelope becomes NaN. Forecast fields propagate NaN, and `BuildView` attempts `utcNow.AddMinutes(NaN)` because its guard excludes only positive infinity.

**User sees:** evaluation failure; whether that terminates the tray app depends on the missing UI exception handling.

**Smallest fix:** require finite utilization within `[0,100]` at both CSV and tracker boundaries. Reject invalid rows without mutation. Validate derived time values before constructing timestamps.

---

**Medium — 10. The WTD floor guarantees long false-red periods after completed bursts**

Locations: `WindowTracker.cs:152–156`; spec `:50–52`.

```text
(01:00:00Z, 60, A)
```

Then every five minutes receive a novel fingerprint with unchanged 60%, and consume nothing further through reset.

Even after the EWMA decays, the session floor is `r >= 60/E`. Therefore:

```text
shortfall >= 300 − 100E/60
```

At **02:50**, after **110 minutes of confirmed zero consumption**:

```text
E = 170 min
r >= 0.35294
shortfall >= 16.67 min
```

The model still returns **DryEarly**, before any additional de-escalation delay. The user finishes the window at 60%.

**Smallest fix:** stop treating WTD pace as a mandatory lower bound on the point forecast. Expose it as a separate historical scenario or uncertainty bound. Base the main forecast on sufficiently recent observed account consumption.

---

**Medium — 11. Poll-count hysteresis treats duplicate replies as independent confirmation**

Locations: `QuotaModel.cs:48–52`; `StateMachine.cs:98–109`.

```text
(02:00:00Z, 10, A)       → Safe
(02:05:00Z, 50, B)       → raw DryEarly, first pending evaluation
(02:05:30Z, 50, B)       → identical cached sample
```

The last sample is rejected by the tracker, but still advances hysteresis and commits **DryEarly**. There has been only one new server observation.

Conversely, six session de-escalation evaluations can take roughly three minutes while burning or fifteen minutes while idle. Weekly twenty-poll de-escalation spans roughly ten versus fifty minutes. The confidence rule changes with polling policy.

Failures do not reset pending evidence either:

```text
02:05:00: first DryEarly observation
02:05:30: failure
02:06:30: failure
02:08:30: duplicate B → commits DryEarly
```

**Smallest fix:** distinguish observation acceptance from transport success. Advance evidence-based hysteresis on accepted observations, and use monotonic persistence durations rather than raw poll counts. Explicitly define whether failures interrupt persistence.

For escalation, count “at least Tight” evidence separately from exact target-state agreement; otherwise alternating Tight/DryEarly can leave Safe committed indefinitely.

---

**Medium — 12. Measuring exposes forecast fields that its contract promises to suppress**

Locations: `QuotaContracts.cs:21–24`; `QuotaModel.cs:152–168`; `WindowTracker.cs:164–167`.

```text
(00:05:00Z, 20, A)
```

Elapsed time is below ten minutes, so state is Measuring. Nevertheless, the returned view contains rate **4%/min**, projected utilization **1200%**, and a depletion timestamp.

A renderer following the documented nullable-field contract can draw a confident wedge during Measuring.

There is also a time-only refusal bug:

```text
(00:09:00Z, 3, A)
(00:09:30Z, 3, A)  // duplicate
(00:10:30Z, 3, A)  // duplicate
```

The tracker remains unseeded with `SampleCount=1`; duplicate polls never seed it even though elapsed time now permits a seed. The spec’s claim that the sample-count clause cannot independently block is false for this path.

**Smallest fix:** null all forecast display fields while Measuring. Make seed eligibility an explicit state transition that can occur when time thresholds become satisfied, without inventing a new consumption sample.

---

**Medium — 13. “Successful poll” does not mean usable data for either window**

Locations: `QuotaModel.cs:48–59,134–141`; `WindowTracker.cs:62`; `FreshnessOracle.cs:32–41`.

At the model boundary:

```text
(02:00:00Z, 20, "not-a-date")
```

With no weekly data, both trackers reject the data, yet `_everSucceeded=true`. Evaluate returns **Live** with no quota information.

For partial degradation, ingest valid session and weekly data at 02:00. Thereafter, every five minutes provide fresh valid session fingerprints but null weekly fields. At 02:25 the old weekly forecast remains attached to a globally **Live** view.

The missing parser may prevent the first case in production; it cannot be established from the supplied files. The model itself does not enforce validity.

**Smallest fix:** return ingest outcomes and maintain validity/freshness per window. Define missing weekly data explicitly as unavailable or stale. Do not substitute zero for a missing percentage with a present reset.

---

**Medium — 14. Warm start silently disappears after an initial failed poll**

Location: `QuotaModel.cs:62–64,72–82`.

```text
02:00:00: IngestFailure(...)
02:01:00: first successful snapshot (20%, A)
           WarmStart(snapshot, ...)
           Ingest(snapshot, ...)
```

`IngestFailure` already set `_ingestCalled`, so WarmStart does nothing. This contradicts “after the first successful poll” and removes potentially essential recent burst history.

**Smallest fix:** track whether live data has been ingested separately from whether any poll finished. Permit warm start before the first successful ingestion, and mark replay completion explicitly.

---

**Medium — 15. Spent claims confirmed blocking without evidence of blocking**

Locations: `StateMachine.cs:21`; spec `:84,118–122`.

```text
(03:20:00Z, 99.6, A)
```

Assume no further consumption and requests remain serviceable within the remaining 0.4%. The model immediately reports **Spent** and `BlockedUntil=05:00`.

The supplied facts do not establish that 99.5% is an enforcement threshold. A monotone envelope makes this unsupported blocking claim persistent.

**Smallest fix:** use “almost exhausted” for the percentage threshold. Reserve confirmed Spent for an explicit quota rejection or a validated source exhaustion indicator. Treat the reset timestamp as an expected recovery deadline until rollover behavior is observed.

---

**Medium — 16. Absolute Safe/DryEarly promises are not identifiable from these observations**

Locations: spec `:48–54,69–76,88`; `QuotaContracts.cs:24`.

```text
(02:00:00Z, 20, A) → immediate Safe
```

Two continuations share that exact history:

- No more consumption: finishes at 20%.
- Another account session consumes 80 points before the next server refresh: user is cut off.

No estimator using only this history can distinguish them. Similarly, the worked-example first sample at 56% can describe a completed batch that never consumes another point.

**Smallest fix:** label forecasts conditionally: “At the observed pace…” Include observation age and an uncertainty indication. Reject the spec’s interpretation that a computable one-point WTD seed is sufficient evidence for a confident future verdict.

This is an unavoidable information limit; the avoidable bug is presenting a conditional extrapolation as certainty.

**Tests I would challenge**

- **`GoldenReplayTests.cs:46–48`:** “No rollover” is guaranteed by filtering to one exact key first. It cannot discover reset jitter. The supplied September 9 rows demonstrate precisely the case that filtering hides.
- **`GoldenReplayTests.cs:54–55`:** finding a percentage below the final maximum does not establish a regression. An entirely increasing sequence passes.
- **`GoldenReplayTests.cs:59–62`:** positive rate and “Safe or Tight” are weak plausibility checks, not an independently justified forecast oracle.
- **`WindowTrackerTests.cs:16–38`:** the constant-rate test examines `max(EWMA,WTD)`, where WTD is constructed to equal the answer. It does not independently verify the EWMA behavior.
- **`WindowTrackerTests.cs:54–61`:** explicitly requiring 99% to be discarded under a reused fingerprint tests the dedup decision, not its safety. A fingerprint/payload inconsistency should trigger uncertainty or validation, not quietly preserve 20%.
- **`StateMachineTests.cs:11–21`:** explicitly blesses Safe with a predicted three-minute cutoff. That is evidence against the spec.
- **`FreshnessOracleTests.cs:9–18`:** fixed `c` isolates away both main integration bugs: adaptive backoff and shrinking reset caps.
- **`QuotaModelTests.cs:129–131`:** a zero or negative delay would satisfy both inequalities. Assert positivity, the actual deadline, and behavior after that deadline.
- **`QuotaModelTests.cs:135–145`:** backoff is tested without reset data, so it misses the cap defeating backoff.
- There are **no supplied WarmStart integration tests** for initial failure, future rows, rebooted monotonic time, equal timestamps, or replay/live deduplication.

The first regression suite should encode findings 1–9 as end-to-end model traces, including UI-timer evaluations between polls. Add sleep spanning reset with the first resumed response still reporting the old window: the model must enter an explicit awaiting-reset condition instead of presenting an expired forecast as current.

## Checked and found sound

- Within a correctly identified window, ordinary finite percentage regressions cannot lower the envelope.
- Repeated fingerprints do not double-count consumption in the tracker.
- An older, genuinely distinct window returned **after** accepting a newer window cannot roll the tracker backward.
- Exact-key CSV filtering excludes genuinely different reset keys; I found no unconditional previous-window leakage through that filter.
- For valid finite inputs, the forecast algebra and worked-example arithmetic are consistent.
- The ratio update preserves a constant rate when `dP = rate × dt` and the seed matches.
- The one-second floor prevents zero/negative delays for ordinary valid dates, although it causes the post-reset retry problem.
- `BlockedUntil` correctly selects the latest reset among windows classified Spent.
- Freshness is recomputed by Evaluate rather than frozen at ingestion. The UI timer’s continued operation and actual poll cancellation remain unverified because those files were not supplied.

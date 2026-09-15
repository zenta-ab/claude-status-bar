Static review of the supplied text only; no files, commands, or tests were run. **I found high-severity gaps in clock handling, replay, validity, and rollover acceptance.** No critical finding established.

References below use the supplied filenames and line numbers. Unless specified otherwise, triples are `(utcNow, pct, resets_at raw)`, dates are **2026-01-01**, and monotonic time advances with UTC.

Define:

```text
A = "2026-01-01T05:00:00.000000+00:00"
B = "2026-01-01T05:00:00.100000+00:00"
C = "2026-01-01T05:00:00.200000+00:00"
D = "2026-01-01T10:00:00.000000+00:00"
```

**Decision-by-decision verification**

| Decision row | Verdict and implementation evidence |
|---|---|
| **1 — Jitter and plausible rollover** | **Partial.** ±120-second jitter and the plausibility comparison are implemented in `WindowTracker.cs:190–198`; ordinary jitter preserves the envelope. The safety valve does **not** require consecutive qualifying polls, and rollover bypasses timing validation. See finding 1. |
| **2 — Grace never turns cutoff into Safe** | **Implemented as a cap.** `StateMachine.cs:45–49` only lowers to Tight and exempts Spent. It does not guarantee that an already-committed Safe verdict agrees with a newer forecast while escalation is pending; that is the separate, specified hysteresis behavior. |
| **3, 5 — Frozen freshness deadlines** | **Implemented for ordinary advancing UTC, partial overall.** `QuotaModel.cs:91–93` freezes policy-derived deadlines; failures leave them untouched at `:96–104`. However, the deadlines and ages remain UTC-based, contrary to decision 6. See finding 2. |
| **4 — One reset poll, then 30-second recovery** | **Partial.** Past reset targets stop capping at `QuotaModel.cs:320–337`, fixing the endless one-second loop for a fixed deadline. An expired Spent hold instead selects 300 seconds at `:293–300`. See finding 8. |
| **6 — Monotonic durations and accepted-live anchor** | **Incorrect.** Only estimator deltas use monotonic time. Freshness, burning, cooldowns, idle and rollover age still use UTC. Every successful-response call re-anchors, including rejected samples: `QuotaModel.cs:88–89`. See finding 2. |
| **7 — UTC-only replay and atomic acceptance** | **Partial.** Replay does not persist CSV monotonic epochs: `WindowTracker.cs:124,170`. Ordinary same-window timing rejection precedes envelope mutation. But candidate classification mutates pending state before acceptance, and rollover skips the timing check: `:108–125,200–205`. See finding 1. |
| **8 — Replay time bounds** | **Literal cutoff implemented, safety incomplete.** `CsvReplay.cs:73–86` excludes rows after `now+60s` and before the truncated key’s start. The permitted future minute can still override live data. See finding 5. |
| **9 — Invalid numbers and guarded timestamps** | **Partial.** Percentage rejection is correct at `WindowTracker.cs:105` and `CsvReplay.cs:123–126`; depletion timestamps use `SafeAddMinutes`. Other timestamp arithmetic remains unguarded, and session validity omits percentage validation. See findings 3 and 10. |
| **10 — Floor decays only with confirmed idle** | **Incorrect.** `WindowTracker.cs:295–297` uses `utcNow−LastRiseAt`, so it decays without any new observation. See finding 7. |
| **11 — Accepted-observation hysteresis** | **Partial.** Acceptance gates at `QuotaModel.cs:79–80` correctly exclude duplicates and failures. Escalation counts and de-escalation evidence are implemented at `StateMachine.cs:120–156`. Persistence and cooldown durations are UTC, not monotonic. See finding 2. |
| **12 — Measuring fields null; time-only seed eligibility** | **Partial.** Nullable forecast fields are suppressed at `QuotaModel.cs:231–237`, and tracker refusal is rechecked at `WindowTracker.cs:265–269`. But `FinalState` never releases the committed Measuring state on time alone. Also, Shortfall is nonnullable and remains zero. See findings 4 and 11. |
| **13 — Per-window validity; Live requires valid session** | **Partial.** Missing weekly data is handled correctly at `QuotaModel.cs:64–75,206`. Session validity checks only the timestamp at `:63`, allowing invalid utilization to retain a verdict and Live freshness. See finding 3. |
| **14 — WarmStart until first successful ingest** | **Partial.** Initial transport failure correctly leaves replay eligible. But `_liveDataIngested=true` is set before any acceptance at `QuotaModel.cs:61`, so an unusable response permanently disables replay. See finding 6. |
| **15 — Spent only at 100%** | **Implemented.** `StateMachine.cs:24` uses 100%; `:30` classifies remaining quota ≤3% as raw DryEarly, subject to refusal, hysteresis and grace. |
| **16 — Conditional forecast copy** | **Unverifiable from these files.** The production forecast formatter/renderers are absent. `QuotaModel.cs:247–257` contains Measuring copy only. The illustrative forecast strings in `docs/forecast-and-states.md:227,231` still lack the promised condition. |
| **Added — Sleep across reset** | **Model state implemented.** `WindowTracker.cs:274` identifies expiration; `QuotaModel.cs:207` forces Measuring, including previously Spent windows. Freshness becomes Stale after 60 seconds at `FreshnessOracle.cs:53–54`, unless already Unknown. Countdown wording cannot be verified without its renderer. Recovery cadence has finding 8. |
| **Added — Weekly refusal and seed minimum of 24 hours** | **Thresholds implemented.** `WindowTracker.cs:89,153,265–267` consistently uses 1440 minutes for weekly and 10 minutes for session. Crossing the threshold with only duplicates still leaves the model Measuring: finding 4. |

**Ranked findings**

**1. High — Nonconsecutive replica replies can trigger a destructive early rollover**

Locations: `WindowTracker.cs:108–125,138–139,186–210`.

```text
(02:00:00Z, 80, A)
(02:01:00Z,  1, D)  // candidate count 1
(02:02:00Z, 80, A)  // duplicate; returns without clearing candidate
(02:03:00Z,  1, D)  // candidate count 2
(02:04:00Z, 80, A)  // duplicate; still does not clear candidate
(02:05:00Z,  1, D)  // count 3: early rollover accepted
```

These are not three consecutive candidate polls. Nevertheless, the model clears 80%, displays **1% / Measuring**, and ignores subsequent correct A-window replies as regressed keys.

Malformed samples and regressed-window replies also leave pending evidence intact. This is especially relevant now that alternating replicas are established behavior.

Separately, rollover is exempted from the timing guard:

```text
(04:59:00Z, 80, A), mono=60_000
(04:58:30Z,  0, D), mono=30_000
```

The second observation is older in both clocks, but passes the old-deadline plausibility test and clears the tracker.

**Smallest fix:** keep candidate bookkeeping separate from accepted estimator state; reset candidate continuity on intervening nonmatching poll outcomes. Validate observation ordering before accepting any rollover, using a live timing baseline that survives window clearing.

**2. High — A cached reply clears clock protection, while durations still use UTC**

Locations: `QuotaModel.cs:88–93,149,178–181,261–265,309–311`; `StateMachine.cs:117,153`; `WindowTracker.cs:254,296`.

```text
Ingest (02:00:00Z, 80, A), mono=0
Evaluate(04:56:00Z, mono=60_000)       → Measuring / Stale
Ingest (04:56:00Z, 80, A), mono=60_000 // cached; tracker rejects
Evaluate(04:56:00Z, mono=60_000)       → Tight / Live
```

The duplicate unconditionally resets the anchor and freshness deadlines. Its data age appears to be 176 minutes, so **this particular forward-jump trace remains Stale via the fingerprint check**, but the confident Tight verdict still returns incorrectly.

The backward case demonstrates false Live directly:

```text
Ingest (02:00:00Z, 80, A), mono=0
Evaluate(01:01:00Z, mono=60_000)       → Measuring / Stale
Ingest (01:01:00Z, 80, A), mono=60_000 // duplicate
Evaluate(01:01:00Z, mono=60_000)       → DryEarly / Live
```

`LastAcceptedUtc` is now in the apparent future. Its age is negative, and the newly frozen deadlines are ahead of the adjusted clock. The rejected sample has restored both a verdict and Live.

Even without a subsequent poll, smaller jumps escape the 60-second discontinuity threshold:

```text
Ingest (02:00:00Z, 20, A), mono=0
Evaluate(02:01:40Z, mono=100_000) → Stale
Evaluate(02:00:51Z, mono=101_000) → Live
```

A 50-second rollback improves freshness without new information.

**User sees:** clock-induced confidence restoration, incorrect ages and countdown-based forecasts. UTC-based hysteresis can also satisfy ten minutes of persistence too early after a forward adjustment or delay it after a rollback.

**Smallest fix:** store freshness deadlines and persistence timestamps in monotonic units; pass monotonic time through hysteresis, burning, idle and rollover-age calculations. Re-anchor only when a live observation is accepted, not merely when a response arrives.

**3. High — Invalid session utilization can retain a confident verdict and Live**

Locations: `QuotaModel.cs:63,71,74,183–184`.

```text
(02:00:00Z, 20,  A) → Safe
(02:00:30Z, NaN, B) → tracker rejects
```

`sessionValid` remains true because B parses as a timestamp. The final view retains **20%, Safe, forecast fields and Live**, instead of Measuring / “Data saknas”.

On a cold model, `(02:00:00Z, NaN, A)` can yield **Live with no session percentage at all**.

**Smallest fix:** include `QuotaTimeUtil.IsValidPct(snapshot.SessionUtilization)` in `sessionValid`, matching weekly validation.

**4. High — Time-only eligibility and replay/live duplicates leave Measuring stuck**

Locations: `QuotaModel.cs:79–80,130–137,204–208`; `WindowTracker.cs:265–269`.

Time-only seed case:

```text
(00:09:00Z, 3, A) → Measuring
(00:09:30Z, 3, A) → duplicate
(00:10:30Z, 3, A) → duplicate
Evaluate(00:10:30Z)
```

The snapshot now has `Refused=false`, but `hold.Committed` is still Measuring. `FinalState` returns it unchanged. The user sees **Measuring with the generic reason**, potentially until the fingerprint changes or the window ends.

The same defect applies to the weekly 24-hour boundary and the five-minute rollover gate.

Replay creates another common route:

```text
CSV:  (02:00:00Z, 20, A)
Live: (02:05:00Z, 20, A)
```

WarmStart seeds the tracker but never initializes hysteresis. The live reply is deduped, so no `StepHysteresis` occurs. A perfectly usable reconstructed window remains Measuring.

**Smallest fix:** when the stored hold is Measuring and the current snapshot becomes eligible, initialize the verdict immediately without advancing evidence-based hysteresis. Apply the same initialization after replay/live deduplication.

**5. High — The allowed future minute still corrupts replay and suppresses live data**

Locations: `CsvReplay.cs:74,86`; `WindowTracker.cs:124–125,144`; `QuotaModel.cs:136–137`.

```text
WarmStart now = 02:10:00Z
CSV:  (02:10:30Z, 100, B)
Live: (02:10:00Z,  20, A)
Next: (02:11:00Z,  20, C)
```

The future CSV row is accepted under the explicit `now+60s` allowance. The first live observation has negative UTC delta and is rejected; replay has already installed a 100% envelope.

Initially the user sees **100% / Measuring** because of finding 4. At the next novel observation, the envelope remains 100% and the hold commits **Spent**, although both live readings report 20%.

This follows decision 8 literally; the decision’s tolerance itself leaves the corruption unresolved.

**Smallest fix:** exclude rows later than the live observation’s timestamp from replay. A timestamp tolerance must not authorize future quota values to take precedence over current live data.

**6. Medium — WarmStart eligibility and membership still lose usable history**

Locations: `QuotaModel.cs:61,120,133–135`; `CsvReplay.cs:84`.

Two independent paths:

- **Unusable first response consumes eligibility.**

  ```text
  Ingest (01:59:00Z, 20, "not-a-date")
  WarmStart for live (02:00:00Z, 20, A)
  ```

  Even with useful CSV history, WarmStart returns immediately because `_liveDataIngested` was set before validation. The user gets a cold estimate.

- **Live jitter tolerance is not used for replay membership.**

  ```text
  CSV:  (02:00:00Z, 10, A)
  CSV:  (02:05:00Z, 90, "2026-01-01T04:59:59.450402+00:00")
  Live: (02:10:00Z, 10, B)
  ```

  The 90% row belongs to the same jitter-tolerant window but is filtered out. The model can show **10% / Safe**. Continuous live ingestion would preserve the 90% envelope.

**Smallest fix:** consume WarmStart eligibility only after usable live ingestion. Use a shared jitter-aware membership rule anchored to the live window when filtering replay history.

**7. Medium — “Confirmed idle” includes time with no confirmation**

Location: `WindowTracker.cs:293–297`.

```text
(01:00:00Z, 60, A)
(01:40:00Z, 60, B) // novel unchanged observation confirms 40 minutes
Evaluate(01:50:00Z) // no additional observation
```

At 01:40, the session floor is approximately:

```text
(60 / 100) × exp(-40/30) = 0.1582 %/min
```

At 01:50 it becomes:

```text
(60 / 110) × exp(-50/30) = 0.1030 %/min
```

The extra ten minutes of exponential idle decay are unsupported by any observation. In this trace the floor exceeds EWMA at both evaluations, so the displayed rate actually changes.

**User sees:** a more optimistic forecast during silence; duplicates also allow this decay while freshness can still be Live. The committed state need not change for the forecast to become misleading.

**Smallest fix:** measure confirmed idle only through the latest accepted unchanged observation, rather than through `utcNow`. Store that duration monotonically.

**8. Medium — Expired Spent state keeps recovery polling at five minutes**

Locations: `QuotaModel.cs:207,293–300`.

```text
(04:59:40Z, 100, A)
(05:00:03Z, 100, A) // cached
NextPollDelay(05:00:03Z)
```

The visible state correctly becomes **Measuring / “Nytt fönster väntas”**, but the hidden hold remains Spent. `PolicyInterval` returns **300 seconds** before checking awaiting reset.

If the new window appears just after this poll, the app can miss recovery for nearly five minutes. Subsequent successes also freeze freshness using the obsolete Spent policy.

**Smallest fix:** treat an expired Spent hold as awaiting reset for scheduling. Preserve 300 seconds only for an actually current blocking window, with an explicit policy for another window awaiting reset.

**9. Low — Demo states bypass the new model invariants**

Locations: `DemoQuotaSource.cs:29,32,57,119–120`.

`safeWeekly` has only `10080−9500=580` elapsed minutes, but is constructed with `refused:false` and shown Safe. The live model must refuse it until 1440 minutes.

The awaiting-reset demo is three minutes past reset but explicitly shows Live at line 57; the live freshness oracle would mark it Stale.

**User sees:** demo combinations that production model rules cannot produce, weakening visual review of the new states.

**Smallest fix:** make the Safe weekly example at least 24 hours old and mark the expired demo Stale.

**10. Low — Valid extreme timestamps still throw**

Locations: `QuotaModel.cs:325,330`; `CsvReplay.cs:73–74`; `FreshnessOracle.cs:53–54`.

```text
(2026-01-01T02:00:00Z, 20,
 "9999-12-31T23:59:59.000000+00:00")
```

The timestamp parses and the tracker accepts it. `Evaluate` computes the poll interval first; adding three seconds to this deadline throws.

Similarly, WarmStart targeting a year-1 deadline can underflow when subtracting the window length. `ReadRows`’ catch does not protect `FilterForWindow`.

**User sees:** model evaluation or warm-start failure; application-level handling is not supplied.

**Smallest fix:** reject deadlines outside arithmetic-safe bounds or use guarded addition for every derived deadline, not only depletion time.

**11. Low — Shortfall cannot satisfy the promised null contract**

Locations: `QuotaContracts.cs:36`; `QuotaModel.cs:236`.

```text
(00:05:00Z, 20, A) → Measuring
```

Rate, pace, projection and depletion are null, but Shortfall is `TimeSpan.Zero`. This violates the explicit decision that **every** forecast display field, including shortfall, is null during Measuring.

**User sees:** a consumer relying on nullability can interpret “unknown shortfall” as “zero blockage”.

**Smallest fix:** make Shortfall nullable and return null for Measuring, including empty and demo views.

**Tests that currently provide false confidence**

- **High — Freshness backoff test is masked by a clock jump.** `QuotaModelTests.cs:260–268` advances UTC by 930 seconds but monotonic time by only five seconds. `Assert.NotEqual(Live)` passes because clock detection forces Stale, even if frozen freshness is broken. Use matching monotonic elapsed time and assert **Unknown**, plus intermediate Stale and pre-deadline Live states.

- **High — Duplicate hysteresis test never checks the duplicate’s effect.** `QuotaModelTests.cs:331–344` only asserts the final escalation. An implementation that incorrectly escalates on the duplicate would also pass. Assert Safe immediately after the duplicate and failures, then the exact expected escalation after the novel observation.

- **High — WarmStart tests can pass with replay entirely disabled.** `QuotaModelTests.cs:405–406` asserts only the live 20%; the comment claims SampleCount verification that is absent. The reboot test at `:461–462` also passes from a cold 50% seed. Assert a forecast independently distinguishable from cold start, and add the identical replay/live fingerprint trace from finding 4.

- **Medium — Weekly threshold test creates a new tracker instead of crossing the threshold.** `WindowTrackerTests.cs:184–191` proves cold eligibility after 24 hours, not recovery of an existing Measuring model. Test one `QuotaModel` across 1439, 1440 and 1441 minutes with duplicate replies, then a novel reply. Assert seed eligibility and final view fields.

- **Medium — Idle tests omit the defining negative case.** `WindowTrackerTests.cs:206–222` supplies a confirming sample, so it cannot catch decay during silence. Add evaluations with no poll, duplicate polls, and accepted unchanged polls. Also, `:234–237` sets reset to `seedAt+300`, making initial elapsed time zero; it does not reconstruct the claimed original `E=60` case.

- **Medium — Equal-timestamp replay test asserts only “no exception”.** `QuotaModelTests.cs:475–493` does not verify which percentage survives, whether live data is accepted, or whether a verdict exists. Sorting by CSV monotonic values at `CsvReplayTests.cs:180–182` proves deterministic ordering, not correct replay across reboot epochs. Define and assert the equal-time precedence rule.

- **Medium — Rollover replay tests cannot establish exit to a verdict.** `RolloverReplayTests.cs:75–84` ends with 0% and less than ten minutes elapsed. Checking that the rollover *reason* disappears does not prove the committed Measuring state can lift. Extend the trace beyond ten minutes with ≥3% usage, including duplicates and timer-only evaluations.

- **Medium — Safety-valve and atomic-rejection coverage is absent.** Add finding 1’s interleaved candidate/duplicate trace, regressed replicas between candidates, invalid intervening polls, and older timestamps carrying plausible later deadlines.

- **Medium — Replay boundaries miss the dangerous interval.** `QuotaModelTests.cs:423` and `CsvReplayTests.cs:119` use ten-minute-future rows. Add `now+1s`, `now+60s`, equal-live timestamps, and cross-second jitter history.

- **Low — Golden checks remain broad.** `GoldenReplayTests.cs:83–88` checks a wide rate range and broad state outcome; roughly 20% consumption alone does not mathematically imply zero shortfall without a rate bound. The final maximum at `:61` also cannot prove the envelope never dipped earlier. Assert per-observation invariants and independently calculated accumulator/forecast checkpoints. The other golden fixture’s contents were not embedded, so its exact expected values cannot be verified here.

- **Low — Deadline tests are only partially strengthened.** `QuotaModelTests.cs:133–134` still accepts nonpositive values. The later test adds positivity near the deadline, but add an exact initial target assertion and the expired-100% case from finding 8.

For ordinary valid inputs and advancing clocks, the ratio-EWMA algebra, monotone envelope, accepted-observation escalation counts, grace cap, 100% threshold, and latest-Spent `BlockedUntil` selection are consistent. The supplied rollover fixture’s old-window replies are correctly rejected after accepting the new window. A fixed, already-past reset target no longer causes the former endless one-second polling loop.

Checked and found sound

---

## Decisions (principal architect, 2026-09-11)

Constraint for this round: **no public contract change** (`IQuotaModel`, `QuotaView`, `WindowView`).
The UI and runtime layers are being hardened in parallel, and the contract is their stable seam.

| # | Finding | Decision |
|---|---|---|
| 1 | Non-consecutive replica replies trigger the rollover safety valve; rollover skips ordering checks | **Accept.** Keep candidate bookkeeping separate from accepted state. Continuity resets on ANY intervening non-matching outcome (an accepted current-window sample, a regressed-window reply, an invalid sample). A rollover must pass the same ordering check as any sample, against a live timing baseline that survives window clearing. |
| 2 | A cached reply re-anchors the clock guard; durations still in UTC | **Accept, no contract change.** Re-anchor only when a live observation is ACCEPTED. Freshness deadlines, hysteresis persistence, burning, idle and since-rollover durations use monotonic time. `NextPollDelay(utcNow)` uses the latest `monoMs` the model received via `Ingest`/`Evaluate`. |
| 3 | Invalid session utilization keeps a verdict and Live | **Accept.** Session validity requires `IsValidPct`, exactly like weekly. |
| 4 | Measuring gets stuck after time-only eligibility, and after a replay→live duplicate (common after every restart) | **Accept.** When the committed state is Measuring and the current snapshot is eligible, commit the verdict immediately, without hysteresis evidence. This covers time-only eligibility, the 5-min rollover gate, the weekly 24 h gate and the replay/live duplicate. |
| 5 | A CSV row up to 60 s in the future still overrides live data | **Accept.** Replay excludes every row later than the first live observation's UTC. |
| 6 | An unusable response consumes WarmStart; replay membership ignores jitter | **Accept.** Only a usable, accepted live ingest consumes WarmStart. Replay membership uses the same ±120 s jitter rule as live ingest. |
| 7 | "Confirmed idle" keeps decaying during silence | **Accept.** Idle is measured through the last accepted unchanged observation (monotonic), never through `now`. There is no decay without evidence. |
| 8 | An expired Spent hold keeps 300 s polling | **Accept.** An expired Spent window schedules as awaiting reset (30 s). |
| 9 | Demo states violate model invariants | **Accept.** The weekly Safe demo gets E ≥ 1440 min, and the awaiting-reset demo is Stale. |
| 10 | Extreme timestamps throw | **Accept.** Reject `resets_at` outside `[now − 1 d, now + 8 d]` at both ingest and replay, and guard all derived deadline arithmetic. |
| 11 | `Shortfall` is non-nullable during Measuring | **Partial.** Keep it non-nullable for contract stability. The model sets `TimeSpan.Zero` in Measuring, and the contract doc states it is meaningless unless State ∉ {Measuring}. Every renderer already gates on State (a UI rule since panel v1). |
| — | Test critiques | **Accept all.** Fix the masked backoff test (matching mono), assert the duplicate's own effect, make WarmStart tests distinguishable from a cold start, cross the weekly threshold on one model with duplicates, add the idle negative cases, define the equal-timestamp precedence, extend the rollover trace past 10 min with ≥ 3 % usage, add safety-valve/ordering traces, add `now+1s`/`now+60s`/equal-live replay boundaries, and assert exact deadline targets and the expired-100 % case. |

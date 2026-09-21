# Codex review — statistics feature (2026-09-21)

## Critical

1. **The downsize recommendation can target a heavily constrained user.**  
   [StatisticsEngine.cs:300](src/Windows/Model/StatisticsEngine.cs:300), [StatisticsEngine.cs:308](src/Windows/Model/StatisticsEngine.cs:308)

   - Concrete input: ten qualifying weekly cycles: seven peaks at 30%, three at 80%, none hitting the *weekly* ceiling. During every week, however, the user hits the five-hour session ceiling daily.
   - Result: `Direction.Downsize`, producing “en mindre plan skulle troligen räcka”.
   - Problem: the guard checks only `HitCeiling` on weekly cycles. It ignores session constraint entirely.
   - Smallest fix: pass qualifying session cycles into this computation and forbid downsizing if any session ceiling was hit—or if session evidence is unavailable. State the evaluated constraints explicitly.

2. **Backfilled and old-plan weeks can recommend changing the current plan.**  
   [StatisticsBackfill.cs:98](src/Windows/Data/StatisticsBackfill.cs:98), [StatisticsEngine.cs:154](src/Windows/Model/StatisticsEngine.cs:154), [StatisticsEngine.cs:300](src/Windows/Model/StatisticsEngine.cs:300)

   - Concrete input: ten backfilled weekly rows with `PlanTier=null`, seven peaks ≤50%; the user subsequently upgrades and has no completed cycle on the new plan.
   - Result: an immediate smaller-plan recommendation about the current account.
   - Problem: percentage-of-budget is not comparable across plan changes, and backfill explicitly lacks plan tier.
   - Smallest fix: recommendations must use only cycles with a known plan tier equal to the currently observed tier. Restart N after a plan change. If the current tier is unknown, suppress plan-size advice.

3. **Hourly consumption assigns pre-observation or gap consumption to the wrong hour.**  
   [StatisticsBackfill.cs:144](src/Windows/Data/StatisticsBackfill.cs:144), [StatisticsBackfill.cs:235](src/Windows/Data/StatisticsBackfill.cs:235), [StatisticsTests.cs:101](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:101)

   - Concrete input: the window is already at 60% when the app starts at 14:00, followed by polls through 14:55 at 60%. The 14:00 hour receives `consumed_pct=60` and enough coverage to qualify.
   - Gap variant: last poll 10% at 09:55, app absent until 14:00 when usage is 80%, then runs all hour. The 70-point unknown-gap increase is credited to 14:00.
   - Result: “peak hour 14” despite no evidence the consumption occurred then.
   - Smallest fix: the first observation of a window establishes the envelope but contributes zero consumption. After a gap over the continuity threshold, re-baseline without assigning the unobserved delta to any hour.

4. **Backfill can erase a concurrent live append.**  
   [StatisticsBackfill.cs:252](src/Windows/Data/StatisticsBackfill.cs:252), [StatisticsBackfill.cs:273](src/Windows/Data/StatisticsBackfill.cs:273), [CycleArchiveCsv.cs:150](src/Windows/Data/CycleArchiveCsv.cs:150)

   - Concrete sequence: backfill reads rows A/B; the live queue appends C; backfill writes its A/B/D snapshot to a temp file and moves it over the archive.
   - Result: live row C disappears. The analogous race exists for hourly files.
   - Problem: merge-plus-atomic-rename is not concurrency-safe, despite the documentation claiming safety against live writes.
   - Smallest fix: serialize live append and backfill rewrite using the same per-account writer/lock, and re-read while holding that lock before replacing the file.

5. **The plan recommendation is exposed after ten weeks, contradicting the stated one-year minimum.**  
   [docs/statistics.md:147](docs/statistics.md:147), [docs/statistics.md:149](docs/statistics.md:149), [StatisticsEngine.cs:302](src/Windows/Model/StatisticsEngine.cs:302)

   - Concrete input: exactly ten fully covered weekly cycles, seven at 30%, three at 80%.
   - Result: a smaller-plan recommendation after roughly 2½ months.
   - Documented promise: “first defensible plan-size statement” at one year.
   - Smallest fix: require both `N >= 10` and at least 365 days of eligible, same-plan history, or revise the documentation and wording to call this a short-history heuristic rather than a defensible plan recommendation.

## High

6. **“Most often weekday/hour” can be selected from one hit and scans 168 cells.**  
   [StatisticsEngine.cs:262](src/Windows/Model/StatisticsEngine.cs:262), [StatisticsEngine.cs:272](src/Windows/Model/StatisticsEngine.cs:272), [StatisticsText.cs:116](src/Windows/Ui/StatisticsText.cs:116)

   - Concrete input: ten qualifying session cycles over three days, only one hit, at Monday 03:00.
   - Result: “oftast måndag kl 03”.
   - Problem: one observation becomes “most often”, and the implementation performs the exact weekday×hour argmax the proposal warns against.
   - Smallest fix: omit the clause unless the hit population and winning bucket independently meet a prespecified threshold. Prefer separate fixed weekday and hour summaries rather than a 168-cell joint scan.

7. **The forecast “accuracy” is only precision among warnings and can hide almost every missed event.**  
   [StatisticsEngine.cs:245](src/Windows/Model/StatisticsEngine.cs:245), [StatisticsText.cs:106](src/Windows/Ui/StatisticsText.cs:106)

   - Concrete input: ten warned cycles all hit the ceiling, plus ninety unwarned cycles that also hit.
   - Result: “100 % rätt, baserat på 10 varningar”.
   - Reality: the warning detected only 10% of ceiling events.
   - Smallest fix: label this “andel varningar som följdes av takträff” and separately report recall over forecast-eligible cycles. For actual calibration, compare `PredictedPeakPct` with outcome/error; it is currently stored but unused.

8. **Forecast eligibility does not require that a prediction exists.**  
   [StatisticsEngine.cs:250](src/Windows/Model/StatisticsEngine.cs:250), [CycleArchiveCsv.cs:92](src/Windows/Data/CycleArchiveCsv.cs:92)

   - Concrete input: ten rows with `WarnedDryEarly=true` but empty `predicted_peak_pct` and `predicted_at_utc`, whether from corruption, an older writer, or a partial migration.
   - Result: the hit-rate tile unlocks.
   - Smallest fix: require both prediction fields, a prediction timestamp inside the cycle and before the outcome, and a schema/source state that distinguishes genuine live capture from unknown history.

9. **The upsize card’s evidence sentence describes the wrong statistic and overstates actual ceiling hits.**  
   [StatisticsEngine.cs:306](src/Windows/Model/StatisticsEngine.cs:306), [StatisticsText.cs:127](src/Windows/Ui/StatisticsText.cs:127), [SwedishText.cs:42](src/Windows/Model/SwedishText.cs:42)

   - Concrete input: six weeks peak at 95%, four at 60%, zero actual ceiling hits.
   - Result: “Du använde högst 50 % … 0 av 10 veckor — du slår ofta i taket”.
   - Problem: the displayed `0 of 10` is unrelated to the upsize decision, and ≥90% is rewritten as “often hit the ceiling”.
   - Smallest fix: for upsize, say “reached at least 90% in 6 of 10 weeks” and use “nära taket”; reserve “slår i taket” for `HitCeiling=true`.

10. **The weekday tile does not measure the most active weekday.**  
    [StatisticsEngine.cs:204](src/Windows/Model/StatisticsEngine.cs:204)

    - Concrete input: ten Mondays each have one covered hour consuming 50%; ten Tuesdays each have eight covered hours consuming 10%.
    - Result: Monday wins because its row mean is 50 versus Tuesday’s 10, although Tuesday consumes 80% per observed day versus Monday’s 50%.
    - Smallest fix: aggregate hourly consumption to local-day totals first, then compare weekday means over those daily observations. Require comparable day coverage.

11. **Completely unobserved competing hours and weekdays do not block a claim.**  
    [StatisticsEngine.cs:211](src/Windows/Model/StatisticsEngine.cs:211), [StatisticsEngine.cs:227](src/Windows/Model/StatisticsEngine.cs:227)

    - Concrete input: the app runs only from 14:00–15:00 on ten days and is always closed otherwise.
    - Result: “Mest aktiva timmen kl 14–15”.
    - Problem: `buckets.All(...)` checks only buckets that exist. Nothing supports comparison with the other 23 hours.
    - Smallest fix: require a declared observation opportunity for every compared bucket, or narrow the wording to “mest använd av de timmar där appen hade tillräcklig täckning”.

12. **The heatmap ignores the coverage gate.**  
    [StatisticsText.cs:170](src/Windows/Ui/StatisticsText.cs:170), [StatisticsText.cs:193](src/Windows/Ui/StatisticsText.cs:193)

    - Concrete input: a session cycle with one observed sample, `CoveredMinutes=0`, `PeakPct=100`, `HitCeiling=true`.
    - Result: a red “nådde taket” day indistinguishable from a fully covered day.
    - Smallest fix: filter heatmap cycles through the same coverage predicate or introduce an explicit incomplete/unknown cell state.

13. **A hit is attributed to the cycle’s start day, not the day it happened.**  
    [StatisticsText.cs:175](src/Windows/Ui/StatisticsText.cs:175)

    - Concrete input: session starts Monday 22:00, reaches 100% Tuesday 02:00, resets Tuesday 03:00.
    - Result: Monday is marked as the day the user hit the ceiling.
    - Smallest fix: for hits, bucket `ResetUtc - BlockedMinutes`; use start day only for non-hit activity/no-data accounting.

14. **Exact blocked time and hit-hour claims are unsupported across gaps.**  
    [StatisticsBackfill.cs:155](src/Windows/Data/StatisticsBackfill.cs:155), [StatisticsBackfill.cs:167](src/Windows/Data/StatisticsBackfill.cs:167), [StatisticsEngine.cs:285](src/Windows/Model/StatisticsEngine.cs:285)

    - Concrete input: 90% at 01:00, no polls for 60 minutes, 100% at 02:00, reset 05:00. Otherwise the cycle has enough coverage to clear 80%.
    - Result: `blocked_minutes=180` and “most often … kl 02”.
    - Reality: the ceiling could have been reached at any point during the gap, so blocked time is only a lower bound and the hour is unknown.
    - Smallest fix: store `ceiling_reached_at` plus certainty, or mark a crossing after a discontinuity as interval-censored. Exclude uncertain crossings from hour-of-hit patterns.

15. **`final_pct` deliberately records replica regression as the final value.**  
    [StatisticsBackfill.cs:157](src/Windows/Data/StatisticsBackfill.cs:157), [StatisticsBackfill.cs:163](src/Windows/Data/StatisticsBackfill.cs:163), [StatisticsTests.cs:58](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:58)

    - Concrete input: 20, 100, then replica-skewed 95 before rollover.
    - Result: `peak_pct=100`, `final_pct=95`.
    - Problem: the feature’s premise says usage does not truly decrease and the monotone envelope handles regressions, but `final_pct` bypasses that rule.
    - Smallest fix: define final as the final envelope value. If the raw last reading is diagnostically valuable, store it separately as `last_raw_pct`.

16. **DST fall-back uses days for N but rows for the reported mean.**  
    [StatisticsEngine.cs:220](src/Windows/Model/StatisticsEngine.cs:220), [StatisticsTests.cs:417](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:417)

    - Concrete input: nine ordinary local 02:00 days at 90%, and the fall-back day’s two 02:00 UTC hours at 0% and 100%.
    - Result: N is ten days, but the average is over eleven rows.
    - Problem: the displayed sample unit and estimator do not match. A fall-back day gets twice the weight.
    - Smallest fix: aggregate both repeated hours into one local-day/hour observation, then average those ten daily observations. Specify whether that daily value is total consumption or duration-normalized rate.

## Medium

17. **Coverage grants observed minutes to unobserved gaps.**  
    [StatisticsBackfill.cs:159](src/Windows/Data/StatisticsBackfill.cs:159), [StatisticsBackfill.cs:230](src/Windows/Data/StatisticsBackfill.cs:230)

    - Concrete input: samples every ten minutes for a whole hour. Six intervals contribute 60 covered minutes even though only instantaneous samples exist.
    - More adversarially, a 60-minute outage contributes ten covered minutes.
    - Result: marginal cycles/hours can cross the 80% gate with fabricated coverage.
    - Smallest fix: define a cadence-based continuity allowance and count only intervals no larger than it; gaps above it should contribute zero, not the cap. Store “estimated coverage” if interpolation remains intentional.

18. **Period filtering excludes cycles that overlap the selected period.**  
    [StatisticsDataLoader.cs:33](src/Windows/Data/StatisticsDataLoader.cs:33)

    - Concrete input: `now=Sep 21`, two-week cutoff Sep 7; a weekly cycle starts Sep 5 and resets Sep 12.
    - Result: the entire completed week is omitted despite five of its seven days lying in the selected period.
    - Smallest fix: use `ResetUtc > cutoff` for closed-cycle outcome statistics, or clearly label presets as “cycles that started during…”.

19. **Jitter tolerance can ratchet indefinitely and defeat rollover detection.**  
    [StatisticsBackfill.cs:196](src/Windows/Data/StatisticsBackfill.cs:196), [StatisticsBackfill.cs:199](src/Windows/Data/StatisticsBackfill.cs:199)

    - Concrete input: successive reset keys 05:00:00, 05:01:40, 05:03:20, 05:05:00. Each is only 100 seconds from the updated key, although the first-to-last movement is five minutes.
    - Result: they remain one cycle; its boundary drifts beyond the stated ±120-second tolerance.
    - Smallest fix: compare candidates with a stable canonical key for the group, not the most recent jittered value; separately retain the chosen representative timestamp.

20. **Live/backfill deduplication relies on exact jittered reset timestamps.**  
    [StatisticsBackfill.cs:251](src/Windows/Data/StatisticsBackfill.cs:251)

    - Concrete input: live archive uses reset `05:00:00`; replay settles on `05:00:01` for the same logical window.
    - Result: both rows survive because the key is exact `DateTimeOffset`.
    - Smallest fix: deduplicate using canonicalized window identity or the same ±120-second matching rule, with deterministic preference for the live row.

21. **The alleged byte-compatible contract does not force UTC.**  
    [CsvUtil.cs:24](src/Windows/Data/CsvUtil.cs:24), [CycleArchiveCsv.cs:61](src/Windows/Data/CycleArchiveCsv.cs:61)

    - Concrete input: `DateTimeOffset(2026-09-21 12:00, +02:00)`.
    - Result: writer emits `2026-09-21T12:00:00.0000000+02:00`, contradicting “always UTC”.
    - Smallest fix: format `value.ToUniversalTime()` and reject or normalize non-UTC input on read.

22. **Schema versions are ignored rather than branched or rejected.**  
    [CycleArchiveCsv.cs:78](src/Windows/Data/CycleArchiveCsv.cs:78), [CycleArchiveCsv.cs:80](src/Windows/Data/CycleArchiveCsv.cs:80), [HourlyRollupCsv.cs:51](src/Windows/Data/HourlyRollupCsv.cs:51)

    - Concrete input: a row beginning with schema `2` but otherwise having the current column layout.
    - Result: it is silently parsed as schema 1.
    - Smallest fix: require `f[0] == Schema`; explicitly dispatch supported versions and reject unknown ones.

23. **Malformed booleans silently become false.**  
    [CycleArchiveCsv.cs:87](src/Windows/Data/CycleArchiveCsv.cs:87), [CycleArchiveCsv.cs:91](src/Windows/Data/CycleArchiveCsv.cs:91)

    - Concrete input: `hit_ceiling=true`, `warned_dry_early=x`.
    - Result: a valid row with both fields false.
    - Smallest fix: accept only literal `0` or `1`; reject the row otherwise.

24. **Quoted newline support is claimed but the reader is line-oriented.**  
    [CsvUtil.cs:15](src/Windows/Data/CsvUtil.cs:15), [CycleArchiveCsv.cs:132](src/Windows/Data/CycleArchiveCsv.cs:132)

    - Concrete input: `plan_tier="team\nenterprise"`.
    - Result: the writer emits a multiline CSV record, while `ReadLine()` feeds each physical line separately and loses the row.
    - Smallest fix: either forbid CR/LF in these fields as part of the contract or implement record-aware multiline CSV parsing.

25. **“Byte-compatible” leaves line endings and double rendering platform-dependent.**  
    [CsvUtil.cs:29](src/Windows/Data/CsvUtil.cs:29), [CycleArchiveCsv.cs:117](src/Windows/Data/CycleArchiveCsv.cs:117)

    - Concrete input: Windows writes CRLF via `WriteLine`; a straightforward Swift writer writes LF. Swift’s default decimal rendering also need not reproduce every .NET `double.ToString()` representation.
    - Result: semantically identical files are not byte-identical.
    - Smallest fix: specify and implement explicit UTF-8/no-BOM, `\n` line endings, final-newline policy, and one portable numeric serialization algorithm or fixed precision.

26. **Directory validation accepts any name containing an underscore.**  
    [StatisticsBackfill.cs:78](src/Windows/Data/StatisticsBackfill.cs:78)

    - Concrete inputs: `cache_old`, `foo_bar`, or `uuid_not-a-uuid`.
    - Result: the directory is treated as a real account and can receive archives.
    - Smallest fix: parse exactly two UUIDs separated by one underscore.

## Low / test-quality findings

27. **The hourly test codifies the initial-baseline bug.**  
    [StatisticsTests.cs:101](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:101), [StatisticsTests.cs:121](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:121)

    It expects the first observed 10% to be consumed in that hour, although it may have accumulated before logging began. Replace it with a zero-baseline assertion and add restart/gap cases.

28. **The replica-regression test does not test its stated failure mode.**  
    [StatisticsTests.cs:144](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:144)

    It asserts only that one cycle exists. It does not assert that regressed rows are absent from hourly consumption, coverage, final value, or the new window. Add explicit adversarial values and assertions for every output.

29. **The spring-forward test tests `TimeZoneInfo`, not the statistics engine.**  
    [StatisticsTests.cs:458](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:458)

    No hourly rows are passed through `StatisticsEngine`. The test would pass if engine bucketing were removed. Assert actual engine buckets and N.

30. **CSV round-trip tests are self-consistency tests, not contract tests.**  
    [StatisticsTests.cs:559](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:559)

    The same implementation formats and parses its own output. Missing golden-byte fixtures allow timestamp offsets, line endings, schema handling and numeric spelling to drift together. Add shared C#/Swift golden files and invalid-input vectors.

31. **Recommendation tests omit the dangerous cross-window and cross-plan cases.**  
    [StatisticsTextTests.cs:227](tests/ClaudeStatusBar.Tests/StatisticsTextTests.cs:227)

    The “constrained” test sets a weekly hit only. Missing cases include frequent session hits with low weekly use, unknown backfill tiers, plan changes, and current-plan history below N.

32. **The DST fall-back test enshrines mismatched weighting.**  
    [StatisticsTests.cs:452](tests/ClaudeStatusBar.Tests/StatisticsTests.cs:452)

    It explicitly expects an eleven-row average while displaying N=10 days. Replace it with a test whose estimator and stated observation unit agree.

## Checked and found sound

- Low-coverage cycles and hours are filtered before the engine’s ordinary cycle/hour aggregates.
- Backfilled rows have `WarnedDryEarly=false`, so they do not directly enter the current warning-only forecast denominator.
- Positive deltas use a monotone envelope within continuously observed windows, so ordinary replica percentage regressions do not create negative hourly consumption.
- UTC storage plus UTC-to-local conversion is the correct direction for DST-safe bucketing; the defect is aggregation weighting, not the conversion itself.
- The last unconfirmed cycle and hour are conservatively omitted by backfill.
- Backfill skips the documented bare-account and `_pending` directories for the names tested.
- Re-running backfill without concurrent writes does not duplicate exact archive keys.
- Empty optional values are consistently represented as empty CSV fields.
- The 80% boundary itself is applied consistently (`>= 0.8`).
- Zero-hit ceiling advice and neutral weekly advice are hidden as intended.

Checked and found sound

---

## Decisions (principal architect)

Governing rule for this round: **a statistic the app cannot defend is worse than no statistic.**
When a fix would weaken a claim, weaken the claim.

| # | Decision |
|---|---|
| 1 | **Accept.** Plan advice considers BOTH windows. Downsizing is forbidden if any qualifying session cycle in the period hit the ceiling, or if session evidence is missing. The card states which constraints were evaluated. |
| 2 | **Accept.** Plan advice uses only cycles with a known `plan_tier` equal to the current tier; N restarts after a plan change; unknown current tier → no plan advice. Backfilled rows (no tier) never feed plan advice. |
| 3 | **Accept.** The first observation of a window, and the first after a gap above the continuity threshold, re-baselines the envelope and contributes zero consumption. Unobserved deltas are never assigned to an hour. |
| 4 | **Accept.** Live appends and backfill rewrites go through one per-account writer lock; backfill re-reads under the lock before replacing. |
| 5 | **Accept, as a two-stage rule.** ≥ 10 same-plan qualifying weeks (plus #1's session check): a *hedged* early hint that says so ("tidig indikation, baserat på N veckor"). ≥ 1 year of same-plan history: the plain statement. The spec is updated to match. |
| 6 | **Accept.** Remove the joint weekday×hour "most often" clause entirely (it is the 168-cell scan the spec forbids). The ceiling card reports counts only; weekday and hour stay the separate, fixed tiles with their own thresholds. |
| 7 | **Accept.** Report two numbers, both labelled plainly: how many warnings were followed by a ceiling hit, and how many ceiling hits were warned in advance — over forecast-eligible cycles only. |
| 8 | **Accept.** A cycle is forecast-eligible only with both prediction fields present, `predicted_at_utc` inside the cycle and before the outcome. |
| 9 | **Accept.** Upsize wording: "nådde minst 90 % i N av M veckor — nära taket"; "slår i taket" only for real `hit_ceiling`. |
| 10 | **Accept.** Weekday metric = mean of local-day totals over covered days, not a row mean. |
| 11 | **Accept.** A peak claim requires every compared bucket to have a real observation opportunity meeting the threshold; otherwise the tile stays in learning state, or the wording is narrowed to the observed hours. Prefer the learning state. |
| 12 | **Accept.** The heatmap applies the coverage gate; incomplete days get their own neutral "okänd" cell. |
| 13 | **Accept.** A hit is dated to when the ceiling was reached (`reset − blocked`), not the cycle's start. |
| 14 | **Accept.** Store `ceiling_reached_at` and whether it is exact or interval-censored (crossed during a gap). Censored crossings are excluded from hour-of-hit patterns; `blocked_minutes` is a lower bound when censored. Schema bump. |
| 15 | **Accept.** `final_pct` = final envelope value. |
| 16 | **Accept.** Repeated DST hours collapse into one local day-hour observation; estimator and displayed N use the same unit. |
| 17 | **Accept.** Coverage counts only intervals no larger than the continuity allowance (derived from the poll cadence); larger gaps contribute zero. |
| 18 | **Accept.** Presets select closed cycles by `reset_utc > cutoff`. |
| 19, 20 | **Accept.** Group by a stable canonical key within ±120 s of the group's first key; dedupe live vs backfill with the same rule, preferring the live row. |
| 21 | **Accept.** Write `ToUniversalTime()`; reject non-UTC on read. |
| 22, 23, 24 | **Accept.** Dispatch on schema and reject unknown versions; booleans only `0`/`1`; CR/LF forbidden in fields (contract) and rejected. |
| 25 | **Accept.** Contract: UTF-8 without BOM, `\n` line endings, final newline; percentages written as integers when integral, otherwise invariant with at most 2 decimals; timestamps UTC round-trip. Shared golden fixtures in `tests/fixtures/` that both suites must reproduce byte for byte. |
| 26 | **Accept.** Account directories must match exactly `<uuid>_<uuid>`. |
| 27-32 | **Accept.** Fix or replace the named tests; add golden-byte contract fixtures, cross-window and cross-plan recommendation cases, and engine-level DST tests. |

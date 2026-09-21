using System.Linq;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// docs/statistics.md, decision 5: derives cycles.csv and hourly-YYYY.csv from the
/// window-shape-*.csv files already on disk. Runs once at first run after the update and is
/// re-runnable on demand (StatusBarApplicationContext/a diagnostics command can call Run again
/// at any time). Two properties make that safe:
///
///  - **Idempotent**: candidates are derived fresh every run (a pure function of what's on disk)
///    and merged into any existing archive by (account_key, window, reset_utc) -- within Codex
///    review #20's +-120s tolerance, not exact equality (see MergeCycles) -- a candidate that
///    matches an already-present row (live-written or previously backfilled) is never re-added,
///    so running Run() twice in a row writes nothing the second time, and a live row always wins
///    over a backfill candidate for the same logical window.
///  - **Crash-safe**: every actual write goes through CycleArchiveCsv/HourlyRollupCsv's
///    WriteAllAtomically (temp file in the same directory, flushed, then renamed over the real
///    path) -- a kill mid-write leaves the previous, still-valid file in place, never a half-written one.
///  - **Concurrency-safe** (Codex review #4): MergeCycles/MergeHourly hold this account's own
///    CsvFileLock across the ENTIRE read-merge-write sequence, the same lock AppendRow acquires
///    for a live append -- a live row landing between backfill's read and write can therefore
///    never be lost, because either it lands (and is read) before backfill's lock acquisition, or
///    it blocks until after backfill's write completes and is then read fresh next run.
///
/// The merge (rather than a wholesale overwrite) also matters once this has shipped for a while:
/// by the time anyone re-runs backfill, live tracking (QuotaModel/AccountRuntime) may already have
/// appended real rows to the same files, and a blind overwrite would destroy them.
/// </summary>
public static class StatisticsBackfill
{
    /// <summary>
    /// Codex review #3/#17: the same cadence-based continuity allowance WindowTracker's live
    /// path uses (WindowTracker.ContinuityAllowanceMinutes), duplicated here because backfill
    /// deliberately never routes through WindowTracker (see the class doc's isReplay note in
    /// WindowTracker.cs: warm start seeds the live model, not the archive). A gap no larger than
    /// this is genuine continuous observation; a larger gap contributes ZERO covered minutes
    /// (never capped down to this value) and re-baselines the envelope (the jump it reveals is
    /// never credited to any specific hour -- see GroupWindow's ceiling/consumption tracking).
    /// </summary>
    const double ContinuityAllowanceMinutes = 10.0;

    public readonly record struct ClosedCycleCandidate(
        DateTimeOffset StartedUtc, DateTimeOffset ResetUtc, double PeakPct, double FinalPct,
        bool HitCeiling, double BlockedMinutes, double CoveredMinutes,
        DateTimeOffset? CeilingReachedAtUtc, bool CeilingReachedCensored);

    public readonly record struct ClosedHourCandidate(
        DateTimeOffset HourStartUtc, double ConsumedPct, int Samples, double CoveredMinutes);

    public readonly record struct WindowGroupingResult(
        IReadOnlyList<ClosedCycleCandidate> Cycles, IReadOnlyList<ClosedHourCandidate> Hours);

    public readonly record struct AccountResult(
        string AccountKey,
        int CyclesDerived, int CyclesNewlyWritten, int CyclesHitCeiling,
        int HourlyRowsDerived, int HourlyRowsNewlyWritten);

    /// <summary>
    /// Scans every per-account directory directly under baseLogDir and backfills each. Skips
    /// anything that is not a real per-account directory (docs/statistics.md, "Backfill / legacy
    /// directories" below) entirely -- never even counted. Never throws: a missing baseLogDir
    /// yields an empty result, same "best-effort" contract as every other reader in this file.
    /// </summary>
    public static IReadOnlyList<AccountResult> Run(string baseLogDir)
    {
        var results = new List<AccountResult>();
        if (!Directory.Exists(baseLogDir)) return results;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(baseLogDir).ToList(); }
        catch { return results; }

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            if (!IsBackfillableAccountDirectory(name)) continue;
            results.Add(RunForAccount(dir, name));
        }
        return results;
    }

    /// <summary>
    /// docs/statistics.md, "Backfill / legacy directories": a real per-account directory is
    /// EXACTLY two UUIDs joined by one underscore (accountUuid_organizationUuid -- the current
    /// StateKey scheme). Codex review #26: the old check (any name containing an underscore
    /// anywhere) wrongly accepted "cache_old", "foo_bar", or "uuid_not-a-uuid" as if they were
    /// real account directories; this validates both halves as actual GUIDs. A bare accountUuid
    /// (no underscore at all) is still always skipped -- the legacy pre-split directory that can
    /// mix two real accounts (docs/multi-account.md's identity guard) and cannot be separated
    /// after the fact; "_pending" (identity not yet known) is skipped for the same "not a stable
    /// per-account key" reason.
    /// </summary>
    public static bool IsBackfillableAccountDirectory(string directoryName)
    {
        if (string.Equals(directoryName, "_pending", StringComparison.OrdinalIgnoreCase)) return false;
        int underscoreIndex = directoryName.IndexOf('_');
        if (underscoreIndex <= 0 || underscoreIndex >= directoryName.Length - 1) return false;
        string accountPart = directoryName[..underscoreIndex];
        string organizationPart = directoryName[(underscoreIndex + 1)..];
        return Guid.TryParseExact(accountPart, "D", out _) && Guid.TryParseExact(organizationPart, "D", out _);
    }

    public static AccountResult RunForAccount(string accountLogDir, string accountKey) =>
        RunForAccount(accountLogDir, accountKey, rebuild: false);

    /// <summary>
    /// One-time migration utility (Codex review round, 2026-09-21): the archive on disk may have
    /// been derived under an EARLIER, flawed version of these rules (#3/#15/#17/#19, among
    /// others, changed what a backfilled row should contain). Unlike Run/RunForAccount
    /// (idempotent, ADD-ONLY merge -- the correct steady-state behaviour once the archive is
    /// already correct), Rebuild DISCARDS every existing BACKFILL-DERIVED row (identified by
    /// IsBackfillDerived: it carries none of the live-only calibration fields -- exactly what
    /// backfill itself always leaves empty) and re-derives them fresh from window-shape-*.csv
    /// under the CURRENT rules, while PRESERVING every LIVE-WRITTEN row untouched -- merged by
    /// the same canonical +-120s window-identity rule (#19/#20), preferring the live row.
    /// window-shape-*.csv itself is only ever read here, never modified.
    /// </summary>
    public static IReadOnlyList<AccountResult> Rebuild(string baseLogDir)
    {
        var results = new List<AccountResult>();
        if (!Directory.Exists(baseLogDir)) return results;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(baseLogDir).ToList(); }
        catch { return results; }

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            if (!IsBackfillableAccountDirectory(name)) continue;
            results.Add(RunForAccount(dir, name, rebuild: true));
        }
        return results;
    }

    /// <summary>A row backfill itself could have produced: none of the live-only calibration fields are set. Backfill never writes plan_tier/warned_dry_early=1/predicted_peak_pct, so any row carrying one of these was written by the LIVE path and must never be discarded by Rebuild.</summary>
    static bool IsLiveWritten(CycleArchiveCsv.Row r) => r.PlanTier != null || r.WarnedDryEarly || r.PredictedPeakPct is not null;

    static AccountResult RunForAccount(string accountLogDir, string accountKey, bool rebuild)
    {
        var rows = new List<CsvReplay.RawRow>();
        foreach (string file in SafeEnumerateWindowShapeFiles(accountLogDir))
            rows.AddRange(CsvReplay.ReadRows(file));
        rows.Sort((a, b) => a.UtcIso.CompareTo(b.UtcIso));

        int cyclesDerived = 0, hitCeiling = 0, hoursDerived = 0;
        var allCycleRows = new List<CycleArchiveCsv.Row>();
        var allHourRows = new List<HourlyRollupCsv.Row>();

        foreach (WindowKind kind in Kinds)
        {
            double windowMinutes = kind == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes;
            WindowGroupingResult grouped = GroupWindow(rows, kind, windowMinutes);

            cyclesDerived += grouped.Cycles.Count;
            hitCeiling += grouped.Cycles.Count(c => c.HitCeiling);
            allCycleRows.AddRange(grouped.Cycles.Select(c => new CycleArchiveCsv.Row(
                accountKey, kind, c.StartedUtc, c.ResetUtc, c.PeakPct, c.FinalPct, c.HitCeiling,
                c.BlockedMinutes, c.CoveredMinutes, PlanTier: null, WarnedDryEarly: false,
                PredictedPeakPct: null, PredictedAtUtc: null,
                CeilingReachedAtUtc: c.CeilingReachedAtUtc, CeilingReachedCensored: c.CeilingReachedCensored)));

            hoursDerived += grouped.Hours.Count;
            allHourRows.AddRange(grouped.Hours.Select(h => new HourlyRollupCsv.Row(
                accountKey, kind, h.HourStartUtc, h.ConsumedPct, h.Samples, h.CoveredMinutes)));
        }

        // Both kinds' candidates are collected BEFORE any write: Rebuild's own write discards
        // every non-live-written row up front, so writing once per kind (as Merge safely does --
        // Merge never discards anything, so per-kind calls are idempotent) would let the SECOND
        // kind's rebuild wipe out the FIRST kind's freshly-added (necessarily non-live) rows.
        int cyclesNew = rebuild ? RebuildCycles(accountLogDir, allCycleRows) : MergeCycles(accountLogDir, allCycleRows);
        int hoursNew = rebuild ? RebuildHourly(accountLogDir, allHourRows) : MergeHourly(accountLogDir, allHourRows);

        return new AccountResult(accountKey, cyclesDerived, cyclesNew, hitCeiling, hoursDerived, hoursNew);
    }

    static readonly WindowKind[] Kinds = { WindowKind.Session, WindowKind.Weekly };

    static IEnumerable<string> SafeEnumerateWindowShapeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "window-shape-*.csv").ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// One pass over one window kind's rows (already sorted by UtcIso), grouping them into
    /// closed windows AND closed clock hours together -- both need the identical rollover
    /// detection, so this does it once and feeds both. Mirrors WindowTracker's live rollover/
    /// coverage/hour-bucket/envelope-consumption logic exactly, but as a pure function over
    /// historical rows instead of live per-poll mutation -- see WindowTracker.cs's isReplay doc
    /// note for why backfill deliberately never routes through WindowTracker itself.
    ///
    /// Codex review #19: window-membership is judged against a CANONICAL key -- the group's own
    /// FIRST observed deadline -- never the most recently jittered one. Comparing against a
    /// ratcheting "latest key" let a boundary drift past the +-120s tolerance one small jitter at
    /// a time (05:00:00 -&gt; 05:01:40 -&gt; 05:03:20 -&gt; 05:05:00, each step &lt;=120s from the
    /// previous but the whole span 5 minutes). The LATEST key is still tracked separately and
    /// becomes the emitted cycle's own ResetUtc (the existing "keep the latest raw deadline"
    /// convention, unchanged).
    ///
    /// Codex review #3/#17: the first row of a group (or the first after a gap beyond the
    /// continuity allowance) re-baselines the running envelope used for both window/hour coverage
    /// and hour consumption -- see the per-field comments below.
    ///
    /// The LAST group (window still open) and the LAST hour bucket (hour not yet closed) at the
    /// end of the data are never emitted -- there is no confirmed close for either in this data,
    /// exactly like the live tracker never emits one until the NEXT poll proves the boundary
    /// passed.
    /// </summary>
    public static WindowGroupingResult GroupWindow(IReadOnlyList<CsvReplay.RawRow> rowsSorted, WindowKind kind, double windowMinutes)
    {
        var cycles = new List<ClosedCycleCandidate>();
        var hours = new List<ClosedHourCandidate>();

        DateTimeOffset? canonicalKey = null; // the group's own FIRST observed deadline -- what new candidates are measured against (#19)
        DateTimeOffset? groupKey = null; // the LATEST observed deadline -- becomes the cycle's own ResetUtc
        var groupRows = new List<(DateTimeOffset Utc, double Pct)>();

        double envelope = 0.0;
        DateTimeOffset? lastEnvelopeUtc = null; // #3: last accepted (window-continuity) sample -- null means "re-baseline the next one"
        DateTimeOffset? hourBucketStart = null;
        DateTimeOffset? hourLastPollUtc = null;
        double hourConsumedPct = 0.0;
        int hourSamples = 0;
        double hourCoveredMinutes = 0.0;

        void CloseCycleGroup()
        {
            if (groupKey is not { } key || groupRows.Count == 0) return;
            double peak = 0.0, covered = 0.0;
            DateTimeOffset? ceilingAt = null;
            bool ceilingCensored = false;
            DateTimeOffset? lastUtc = null;
            foreach ((DateTimeOffset utc, double pct) in groupRows)
            {
                bool rebaseline = lastUtc is not { } prevUtc || (utc - prevUtc).TotalMinutes > ContinuityAllowanceMinutes;
                if (lastUtc is { } last && utc >= last)
                {
                    double gapMinutes = (utc - last).TotalMinutes;
                    if (gapMinutes <= ContinuityAllowanceMinutes) covered += gapMinutes;
                    // else: a gap beyond the allowance contributes nothing (#17) -- never capped.
                }
                lastUtc = utc;
                if (pct > peak) peak = pct;
                if (ceilingAt is null && peak >= 100.0)
                {
                    ceilingAt = utc;
                    ceilingCensored = rebaseline; // #14: exact only when reached under continuous observation
                }
            }
            bool hitCeiling = peak >= 100.0;
            double blocked = hitCeiling && ceilingAt is { } ca ? Math.Max(0.0, (key - ca).TotalMinutes) : 0.0;
            DateTimeOffset started = QuotaTimeUtil.SafeAddMinutes(key, -windowMinutes) ?? key;
            // #15: final = the final envelope value (== peak, since the envelope only rises), never the raw last reading.
            cycles.Add(new ClosedCycleCandidate(started, key, peak, peak, hitCeiling, blocked, covered, ceilingAt, ceilingCensored));
        }

        void CloseHourBucket()
        {
            if (hourBucketStart is { } start)
                hours.Add(new ClosedHourCandidate(start, hourConsumedPct, hourSamples, hourCoveredMinutes));
            hourConsumedPct = 0.0;
            hourSamples = 0;
            hourCoveredMinutes = 0.0;
        }

        foreach (CsvReplay.RawRow row in rowsSorted)
        {
            double? pct = kind == WindowKind.Session ? row.SessionPct : row.WeeklyPct;
            string? raw = kind == WindowKind.Session ? row.SessionResetsAtRaw : row.WeeklyResetsAtRaw;
            if (pct is null || raw is null) continue;
            if (!QuotaTimeUtil.IsValidPct(pct.Value)) continue;
            if (!QuotaTimeUtil.TryParseResetsAt(raw, out DateTimeOffset resetsAt)) continue;

            if (groupKey is not { } currentKey)
            {
                canonicalKey = resetsAt;
                groupKey = resetsAt;
                groupRows.Add((row.UtcIso, pct.Value));
            }
            else
            {
                double diffSeconds = (resetsAt - canonicalKey!.Value).TotalSeconds; // #19: stable canonical key, never the ratcheted latest
                if (Math.Abs(diffSeconds) <= WindowTracker.JitterTolerance.TotalSeconds)
                {
                    groupKey = resetsAt; // keep the latest raw deadline -- same convention as the live tracker
                    groupRows.Add((row.UtcIso, pct.Value));
                }
                else if (diffSeconds > 0)
                {
                    CloseCycleGroup();
                    canonicalKey = resetsAt;
                    groupKey = resetsAt;
                    groupRows.Clear();
                    groupRows.Add((row.UtcIso, pct.Value));
                    envelope = 0.0; // a new window's envelope starts fresh
                    lastEnvelopeUtc = null;
                }
                else
                {
                    continue; // regressed by >120s vs the canonical key: replica noise, ignored entirely -- never joins a group, never counted for coverage/hour bucketing either
                }
            }

            DateTimeOffset hourStart = TruncateToHour(row.UtcIso);
            if (hourBucketStart is { } currentHour && currentHour != hourStart)
            {
                CloseHourBucket();
                hourBucketStart = hourStart;
                hourLastPollUtc = row.UtcIso;
            }
            else if (hourBucketStart is null)
            {
                hourBucketStart = hourStart;
                hourLastPollUtc = row.UtcIso;
            }
            else
            {
                if (hourLastPollUtc is { } lastHourPoll && row.UtcIso >= lastHourPoll)
                {
                    double gapMinutes = (row.UtcIso - lastHourPoll).TotalMinutes;
                    if (gapMinutes <= ContinuityAllowanceMinutes) hourCoveredMinutes += gapMinutes;
                }
                hourLastPollUtc = row.UtcIso;
            }

            // #3: the first observation of the window (lastEnvelopeUtc null -- cold start or just
            // re-baselined after a rollover), or the first after a gap beyond the continuity
            // allowance, contributes zero to this hour's consumption -- the jump still raises the
            // envelope (for the cycle's own peak/ceiling tracking above), but WHEN it happened is
            // unknown, so it is never attributed to this specific poll's clock hour.
            bool envelopeRebaseline = lastEnvelopeUtc is not { } lastE || (row.UtcIso - lastE).TotalMinutes > ContinuityAllowanceMinutes;
            double dP = Math.Max(0.0, pct.Value - envelope);
            envelope = Math.Max(envelope, pct.Value);
            if (!envelopeRebaseline) hourConsumedPct += dP;
            hourSamples++;
            lastEnvelopeUtc = row.UtcIso;
        }

        // The final group/bucket is still open -- no confirmed close in this data, so neither is emitted.
        return new WindowGroupingResult(cycles, hours);
    }

    static DateTimeOffset TruncateToHour(DateTimeOffset utc)
    {
        DateTime u = utc.UtcDateTime;
        return new DateTimeOffset(new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>Codex review #20: two windows are the "same logical window" when their reset_utc values are within the live tracker's own jitter tolerance, not only on exact equality -- a live-written row and a backfill-derived candidate for the same real window can legitimately differ by a few seconds of jitter.</summary>
    static bool SameWindow(string accountKeyA, WindowKind windowA, DateTimeOffset resetA, string accountKeyB, WindowKind windowB, DateTimeOffset resetB) =>
        accountKeyA == accountKeyB && windowA == windowB
        && Math.Abs((resetA - resetB).TotalSeconds) <= WindowTracker.JitterTolerance.TotalSeconds;

    /// <summary>
    /// Merges candidates into cycles.csv by (account_key, window, reset_utc) within +-120s
    /// (Codex review #20), never re-adding a key that already matches an existing row -- whether
    /// that row was live-written or itself a prior backfill candidate. Backfill never overwrites,
    /// so an existing live row always wins by construction: it is simply left in place, never
    /// replaced by a backfill candidate for the "same" window. Returns how many rows were newly
    /// added. Codex review #4: the whole read-merge-write sequence holds this account's
    /// CsvFileLock, so a live append landing mid-merge can never be lost by the rewrite below.
    /// </summary>
    static int MergeCycles(string accountLogDir, IEnumerable<CycleArchiveCsv.Row> candidates)
    {
        string path = Path.Combine(accountLogDir, CycleArchiveCsv.FileName);
        lock (CsvFileLock.For(accountLogDir))
        {
            List<CycleArchiveCsv.Row> existing = CycleArchiveCsv.ReadAll(path).ToList();

            int added = 0;
            foreach (CycleArchiveCsv.Row candidate in candidates)
            {
                bool alreadyPresent = existing.Any(r => SameWindow(r.AccountKey, r.Window, r.ResetUtc, candidate.AccountKey, candidate.Window, candidate.ResetUtc));
                if (!alreadyPresent)
                {
                    existing.Add(candidate);
                    added++;
                }
            }
            if (added > 0) CycleArchiveCsv.WriteAllAtomically(path, existing.OrderBy(r => r.ResetUtc));
            return added;
        }
    }

    /// <summary>
    /// Merges candidates into hourly-YYYY.csv (grouped by year, since that's how the file is
    /// split) by (account_key, window, hour_start_utc) -- exact equality is correct here (unlike
    /// cycles.csv's reset_utc): hour_start_utc is always a canonical truncated clock hour in both
    /// the live and backfill paths, so jitter cannot occur. Returns how many rows were newly
    /// added. Codex review #4: same per-account lock as MergeCycles, held across the whole
    /// read-merge-write sequence for each year's file.
    /// </summary>
    static int MergeHourly(string accountLogDir, IEnumerable<HourlyRollupCsv.Row> candidates)
    {
        int added = 0;
        lock (CsvFileLock.For(accountLogDir))
        {
            foreach (IGrouping<int, HourlyRollupCsv.Row> yearGroup in candidates.GroupBy(r => r.HourStartUtc.UtcDateTime.Year))
            {
                string path = Path.Combine(accountLogDir, HourlyRollupCsv.FileName(yearGroup.Key));
                List<HourlyRollupCsv.Row> existing = HourlyRollupCsv.ReadAll(path).ToList();
                var keys = new HashSet<(string, WindowKind, DateTimeOffset)>(
                    existing.Select(r => (r.AccountKey, r.Window, r.HourStartUtc)));

                int addedThisYear = 0;
                foreach (HourlyRollupCsv.Row candidate in yearGroup)
                {
                    if (keys.Add((candidate.AccountKey, candidate.Window, candidate.HourStartUtc)))
                    {
                        existing.Add(candidate);
                        addedThisYear++;
                    }
                }
                if (addedThisYear > 0)
                    HourlyRollupCsv.WriteAllAtomically(accountLogDir, yearGroup.Key, existing.OrderBy(r => r.HourStartUtc));
                added += addedThisYear;
            }
        }
        return added;
    }

    /// <summary>
    /// Rebuild's own version of MergeCycles: every EXISTING row that is NOT live-written
    /// (IsLiveWritten) is discarded up front (it is stale, derived under an earlier version of
    /// these rules) rather than kept and matched against; the file is then rebuilt from the
    /// surviving live rows plus every freshly-derived candidate that does not match one of them
    /// (same +-120s canonical-window-identity rule as MergeCycles). Always rewrites the file
    /// (even when nothing changed) so a stale backfill-derived row is guaranteed to be replaced.
    /// </summary>
    static int RebuildCycles(string accountLogDir, IEnumerable<CycleArchiveCsv.Row> candidates)
    {
        string path = Path.Combine(accountLogDir, CycleArchiveCsv.FileName);
        lock (CsvFileLock.For(accountLogDir))
        {
            List<CycleArchiveCsv.Row> survivors = CycleArchiveCsv.ReadAll(path).Where(IsLiveWritten).ToList();

            int added = 0;
            foreach (CycleArchiveCsv.Row candidate in candidates)
            {
                bool alreadyPresent = survivors.Any(r => SameWindow(r.AccountKey, r.Window, r.ResetUtc, candidate.AccountKey, candidate.Window, candidate.ResetUtc));
                if (!alreadyPresent)
                {
                    survivors.Add(candidate);
                    added++;
                }
            }
            CycleArchiveCsv.WriteAllAtomically(path, survivors.OrderBy(r => r.ResetUtc));
            return added;
        }
    }

    /// <summary>Rebuild's version of MergeHourly -- hourly-*.csv carries no live-only calibration fields at all, so every row is equally "derived"; Rebuild simply regenerates the whole file from the fresh candidates for each year present in this account's window-shape-*.csv history (a year with no fresh candidates keeps whatever the file already held for OTHER years untouched -- only years that actually have new data get rewritten).</summary>
    static int RebuildHourly(string accountLogDir, IEnumerable<HourlyRollupCsv.Row> candidates)
    {
        int written = 0;
        lock (CsvFileLock.For(accountLogDir))
        {
            foreach (IGrouping<int, HourlyRollupCsv.Row> yearGroup in candidates.GroupBy(r => r.HourStartUtc.UtcDateTime.Year))
            {
                List<HourlyRollupCsv.Row> fresh = yearGroup.OrderBy(r => r.HourStartUtc).ToList();
                HourlyRollupCsv.WriteAllAtomically(accountLogDir, yearGroup.Key, fresh);
                written += fresh.Count;
            }
        }
        return written;
    }
}

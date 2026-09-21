namespace ClaudeStatusBar.Model;

/// <summary>
/// docs/statistics.md, decision 4 "Proactive advice": at most one line in the panel, shown only
/// when one of the two pre-specified recommendations has passed its N threshold AND changed
/// since it was last shown to this account. Pure decision logic -- Data/StatisticsAdviceStore.cs
/// owns reading/writing the small persisted "what was last shown" JSON this depends on.
/// </summary>
public static class StatisticsAdvice
{
    /// <summary>Persisted per account: the signature (see *Signature below) of whichever recommendation was last shown, or null if that recommendation has never fired yet.</summary>
    public readonly record struct LastShown(string? CeilingSignature, string? WeeklyBudgetSignature)
    {
        public static LastShown Empty { get; } = new(null, null);
    }

    public readonly record struct Advice(string Line);

    /// <summary>
    /// A stable string capturing exactly the fields that make the ceiling recommendation a
    /// DIFFERENT statement worth re-surfacing -- not "still true", but "now says something new"
    /// (a new times-hit count or a different weeks-spanned span; Codex review #6 removed the
    /// weekday/hour clause entirely, so it no longer factors in here). Null while the
    /// recommendation hasn't cleared its own N threshold yet, OR while it has cleared it but
    /// TimesHit is 0 -- "0 times" is not advice worth announcing in the panel either, the same
    /// "Nådde taket" tile already says it.
    /// </summary>
    public static string? CeilingSignature(StatisticsEngine.CeilingRecommendation r) =>
        r.Ready && r.TimesHit > 0 ? $"{r.TimesHit}|{r.WeeksSpanned}" : null;

    /// <summary>Same idea as CeilingSignature, for the weekly-budget recommendation's own numbers -- null (task item 3's fix) whenever Direction is None, the neutral middle that gets no card in the statistics window either, so the panel line and the window agree on when there is genuinely something to say.</summary>
    public static string? WeeklyBudgetSignature(StatisticsEngine.WeeklyBudgetRecommendation r) =>
        r.Ready && r.Direction != StatisticsEngine.WeeklyBudgetDirection.None
            ? $"{r.WeeksAtOrBelow}|{r.WeeksConsidered}|{r.Direction}"
            : null;

    /// <summary>
    /// Decides at most one line to show now, and the LastShown state to persist afterward.
    /// Only the recommendation that actually fires has its signature advanced -- the other's
    /// LastShown is carried through unchanged, so it can still fire later once ITS OWN signature
    /// next changes, independent of this tick's winner. The ceiling recommendation is checked
    /// first when both are newly-changed on the same tick (an arbitrary but deterministic
    /// tie-break; docs/statistics.md never ranks the two against each other, and "at most one
    /// line" rules out showing both at once).
    /// </summary>
    public static (Advice? Advice, LastShown NextState) Decide(
        StatisticsEngine.CeilingRecommendation ceiling,
        StatisticsEngine.WeeklyBudgetRecommendation weeklyBudget,
        LastShown lastShown)
    {
        string? ceilingSig = CeilingSignature(ceiling);
        if (ceilingSig != null && ceilingSig != lastShown.CeilingSignature)
            return (new Advice(ComposeCeilingLine(ceiling)), lastShown with { CeilingSignature = ceilingSig });

        string? weeklySig = WeeklyBudgetSignature(weeklyBudget);
        if (weeklySig != null && weeklySig != lastShown.WeeklyBudgetSignature)
            return (new Advice(ComposeWeeklyBudgetLine(weeklyBudget)), lastShown with { WeeklyBudgetSignature = weeklySig });

        return (null, lastShown);
    }

    static string ComposeCeilingLine(StatisticsEngine.CeilingRecommendation r) =>
        $"Statistik: du nådde taket {r.TimesHit} {SwedishText.Times(r.TimesHit)} på {r.WeeksSpanned} {SwedishText.Weeks(r.WeeksSpanned)}.";

    /// <summary>Codex review #9: Downsize and Upsize report DIFFERENT evidence numbers -- Downsize the at-or-below-threshold share, Upsize the at-or-near-ceiling share (never "0 av 10 veckor -- du slår ofta i taket", finding #9's exact bug). Codex review #5: an early-hint prefix whenever the same-plan pool has not yet reached a full year of history.</summary>
    static string ComposeWeeklyBudgetLine(StatisticsEngine.WeeklyBudgetRecommendation r)
    {
        string prefix = r.IsEarlyHint ? SwedishText.EarlyHintPrefix(r.WeeksConsidered) : "";
        string body = r.Direction switch
        {
            StatisticsEngine.WeeklyBudgetDirection.Downsize =>
                $"Statistik: högst {r.ThresholdPct:F0} % av veckobudgeten använd {r.WeeksAtOrBelow} av {r.WeeksConsidered} veckor.",
            StatisticsEngine.WeeklyBudgetDirection.Upsize =>
                $"Statistik: minst {StatisticsEngine.WeeklyBudgetNearCeilingPct:F0} % av veckobudgeten nådd {r.WeeksNearCeiling} av {r.WeeksConsidered} veckor — nära taket.",
            _ => "",
        };
        return $"{prefix}{body}{SwedishText.WeeklyBudgetConclusion(r.Direction)}";
    }
}

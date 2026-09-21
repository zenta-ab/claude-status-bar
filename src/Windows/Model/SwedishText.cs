namespace ClaudeStatusBar.Model;

/// <summary>
/// Shared Swedish word lists for whichever text-composition layer needs a full (not abbreviated)
/// weekday or a month name -- Ui/TimeText.cs already owns the abbreviated weekday forms
/// ("mån", "tis", ...) used throughout the panel; this is the statistics window's own longer
/// forms ("måndag", "januari"), kept here (Model, not Ui) so both Model/StatisticsAdvice.cs (the
/// panel's proactive-advice line) and Ui/StatisticsText.cs can share one spelling.
/// </summary>
public static class SwedishText
{
    /// <summary>Indexed by DayOfWeek (0=söndag .. 6=lördag).</summary>
    public static readonly string[] WeekdayFull = { "söndag", "måndag", "tisdag", "onsdag", "torsdag", "fredag", "lördag" };

    /// <summary>Indexed 1-12.</summary>
    public static readonly string[] MonthAbbrev =
    {
        "", "jan", "feb", "mar", "apr", "maj", "jun", "jul", "aug", "sep", "okt", "nov", "dec",
    };

    /// <summary>"1 gång"/"N gånger" -- the singular/plural word alone, never the leading count.</summary>
    public static string Times(int n) => n == 1 ? "gång" : "gånger";

    /// <summary>"1 vecka"/"N veckor" -- the singular/plural word alone, never the leading count.</summary>
    public static string Weeks(int n) => n == 1 ? "vecka" : "veckor";

    /// <summary>
    /// "senaste veckan" for exactly one week, else "de senaste N veckorna" -- the task's own fix
    /// for the "de senaste 1 veckorna" defect a single-week ceiling recommendation used to
    /// produce (WeeksSpanned can genuinely be 1: a sparse or newly-started account's qualifying
    /// session cycles can all fall inside one calendar week).
    /// </summary>
    public static string RecentWeeksPhrase(int n) => n == 1 ? "senaste veckan" : $"de senaste {n} veckorna";

    /// <summary>Plural, lower-case weekday name for an "N av 10 fredagar"-style count -- every weekday in WeekdayFull pluralizes by adding "ar" (fredag -&gt; fredagar, söndag -&gt; söndagar, ...).</summary>
    public static string WeekdayPluralLower(DayOfWeek weekday) => WeekdayFull[(int)weekday] + "ar";

    /// <summary>
    /// docs/statistics.md decision 3's added conclusion for recommendation 2, worded
    /// conditionally and honest about what the app cannot know: empty for the neutral middle
    /// (Direction.None), which never gets a card at all. Codex review #9: the Upsize wording no
    /// longer says "slår i taket" (reaches/hits the ceiling) -- that phrase is reserved for a
    /// real hit_ceiling=1 event (TimesAtCeiling/CeilingRecommendation). Reaching >=90% of the
    /// weekly budget is being NEAR the ceiling, not hitting it, and the two must never be
    /// conflated in the same sentence.
    /// </summary>
    public static string WeeklyBudgetConclusion(StatisticsEngine.WeeklyBudgetDirection direction) => direction switch
    {
        StatisticsEngine.WeeklyBudgetDirection.Downsize => " — en mindre plan skulle troligen räcka för hur du arbetar nu.",
        StatisticsEngine.WeeklyBudgetDirection.Upsize => " — en större plan eller jämnare fördelning skulle kunna hjälpa.",
        _ => "",
    };

    /// <summary>Codex review #5's two-stage rule: N>=10 same-plan weeks alone is only a hedged early hint, never the plain statement -- prefixed onto the recommendation line whenever WeeklyBudgetRecommendation.IsEarlyHint is true.</summary>
    public static string EarlyHintPrefix(int weeks) => $"Tidig indikation, baserat på {weeks} {Weeks(weeks)}: ";
}

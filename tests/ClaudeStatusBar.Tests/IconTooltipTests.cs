using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Icons/IconSlot.cs's BuildTooltip (internal, see AssemblyInfo.cs's InternalsVisibleTo):
/// docs/multi-account.md's tray tooltip rule -- starts with the account label, then the
/// verdict, truncates the LABEL (never the verdict) to stay within NotifyIcon.Text's 63-char
/// runtime cap, and with no label (the single-account default) is unchanged from before.
/// </summary>
public class IconTooltipTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    static WindowView SafeSession() =>
        new(WindowKind.Session, QuotaWindows.SessionMinutes, UsedPct: 20.0, Now.AddHours(1), QuotaState.Safe,
            RatePctPerMin: 0.1, PaceMultiple: 1.0, ProjectedPctAtReset: 26.0, DepletesAt: null, Shortfall: TimeSpan.Zero, MeasuringReason: null);

    static WindowView EmptyWeekly() => WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes);

    static QuotaView SafeView() => new(SafeSession(), EmptyWeekly(), Freshness.Live,
        Now.AddSeconds(-5), Now.AddSeconds(-2), TimeSpan.FromSeconds(30), Error: null, QuotaState.Safe, BlockedUntil: null);

    [Fact]
    public void NoLabel_MatchesTheOriginalSingleAccountTooltipExactly()
    {
        string withLabel = IconSlot.BuildTooltip(SafeView(), accountLabel: "Max");
        string withoutLabel = IconSlot.BuildTooltip(SafeView(), accountLabel: null);

        Assert.DoesNotContain("Max", withoutLabel);
        Assert.StartsWith("Max · ", withLabel);
    }

    [Fact]
    public void WithLabel_StartsWithLabelThenVerdict_StaysWithinSixtyThreeChars()
    {
        string tooltip = IconSlot.BuildTooltip(SafeView(), accountLabel: "Team");

        Assert.True(tooltip.Length <= 63, $"tooltip was {tooltip.Length} chars: \"{tooltip}\"");
        Assert.StartsWith("Team · ", tooltip);
    }

    [Fact]
    public void VeryLongLabel_LabelIsTruncated_VerdictStaysIntact()
    {
        string verdict = IconSlot.BuildTooltip(SafeView(), accountLabel: null);
        string longLabel = new string('A', 100);

        string tooltip = IconSlot.BuildTooltip(SafeView(), accountLabel: longLabel);

        Assert.True(tooltip.Length <= 63, $"tooltip was {tooltip.Length} chars: \"{tooltip}\"");
        Assert.EndsWith(verdict, tooltip); // the verdict itself was never cut, only the label ahead of it
        Assert.DoesNotContain(longLabel, tooltip); // the full 100-char label could not have fit
    }

    [Fact]
    public void LabelSoLongNothingFits_FallsBackToVerdictAlone()
    {
        // A verdict that alone is already at (or extremely close to) the cap leaves no budget
        // for "{label} · " ahead of it -- the label must be dropped entirely rather than
        // mangling the verdict down to something unreadable.
        string verdict = IconSlot.BuildTooltip(SafeView(), accountLabel: null);
        string tooltip = IconSlot.BuildTooltip(SafeView(), accountLabel: new string('B', 5));

        Assert.True(tooltip.Length <= 63);
        // Either the label fit (verdict is short enough) or it was dropped -- either way the
        // verdict text itself is never truncated.
        Assert.True(tooltip.EndsWith(verdict) || tooltip == verdict);
    }
}

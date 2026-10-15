using System.Drawing;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// The timeline strip's two display details: the row labels (sized to the widest label up to ~30 % of the strip, shortened in
/// the MIDDLE so the distinguishing end survives, never identical for two different labels) and the hover popover (opaque,
/// painted last, placed so it never covers the hovered segment and always stays inside the container).
/// </summary>
public class TimelineHoverAndLabelTests
{
    // a fixed-width "font": 6 px per character, so widths are exact
    static float Measure(string s) => s.Length * 6f;

    // ---- the labels ----

    [Fact]
    public void LabelsThatFit_AreLeftAlone()
    {
        var shown = TimelineText.FitLabels(new[] { "Personligt", "Team", "A" }, Measure, 100f);

        Assert.Equal(new[] { "Personligt", "Team", "A" }, shown);
    }

    [Fact]
    public void ALongLabel_IsShortenedInTheMiddle_SoItsEndSurvives()
    {
        var shown = TimelineText.FitLabels(new[] { "Max 20x (alex@example.com)" }, Measure, 100f);

        string text = shown[0];
        Assert.Contains("…", text);
        Assert.True(Measure(text) <= 100f, $"\"{text}\" is {Measure(text)} px");
        Assert.StartsWith("Max", text);                // the start is kept ...
        Assert.EndsWith("le.com)", text);              // ... and the distinguishing end
        Assert.False(text.EndsWith("…"));
    }

    [Fact]
    public void TwoLabelsWithALongSharedPrefix_AreNeverTheSameText()
    {
        var shown = TimelineText.FitLabels(new[] { "Max 20x (alex@example.com)", "Max 20x (alex@example.org)" }, Measure, 100f);

        Assert.NotEqual(shown[0], shown[1]);
        Assert.EndsWith(".com)", shown[0]);
        Assert.EndsWith(".org)", shown[1]);
        Assert.All(shown, s => Assert.True(Measure(s) <= 100f));
    }

    [Fact]
    public void LabelsThatDifferOnlyInTheMiddle_GetALongerTailUntilTheyDiffer()
    {
        var labels = new[] { "Max 20x (alex1@example.com)", "Max 20x (alex2@example.com)", "Max 20x (alex3@example.com)" };

        var shown = TimelineText.FitLabels(labels, Measure, 100f);

        Assert.Equal(3, shown.Distinct().Count());
        Assert.All(shown, s => Assert.True(Measure(s) <= 100f, s));
        Assert.Contains("1@example.com)", shown[0]);   // the tail reaches back to the character that tells them apart
    }

    [Fact]
    public void ManyAccountsWithTheSameShape_StayDistinct()
    {
        var labels = Enumerable.Range(1, 12).Select(i => $"Max 20x (user{i}@example.com)").ToArray();

        var shown = TimelineText.FitLabels(labels, Measure, 100f);

        Assert.Equal(labels.Length, shown.Distinct().Count());
        Assert.All(shown, s => Assert.True(Measure(s) <= 100f, s));
    }

    [Fact]
    public void IdenticalLabels_CannotBeToldApart_ButNothingBreaks()
    {
        var shown = TimelineText.FitLabels(new[] { "Max 20x (alex@example.com)", "Max 20x (alex@example.com)" }, Measure, 100f);

        Assert.Equal(shown[0], shown[1]);
    }

    [Fact]
    public void AShortenedLabel_NeverEqualsADifferentWholeOne()
    {
        const string first = "Max 20x (alex@example.com)";
        string shortened = TimelineText.FitLabels(new[] { first }, Measure, 100f)[0];    // what the first one would be written as alone ...

        var shown = TimelineText.FitLabels(new[] { first, shortened }, Measure, 100f);    // ... and another account whose whole label is exactly that

        Assert.Equal(shortened, shown[1]);                    // the whole one is never touched
        Assert.NotEqual(shown[0], shown[1]);                  // the long one gives way
    }

    // ---- the label column of a real layout ----

    static TimelineStrip.Layout Layout(float width, params string[] labels)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var label = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var answer = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var caption = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        return TimelineStrip.Measure(g, width, labels, "Ingen lucka de närmaste 24 h", new TimelineFonts(answer, label, small, caption),
            (gr, text, font, color, x, y, w, draw) => gr.MeasureString(text, font, (int)w).Height);
    }

    [Fact]
    public void TheLabelColumn_IsAsWideAsTheWidestLabel_UpToAThirdOfTheStrip_AndLongLabelsFitUnshortened()
    {
        string[] labels = { "Max 20x (alex@example.com)", "Max 20x (alex@example.org)", "Team (sam@example.net)" };

        TimelineStrip.Layout wide = Layout(1020f, labels);
        TimelineStrip.Layout narrow = Layout(340f, labels);
        TimelineStrip.Layout shortLabels = Layout(1020f, "A", "B");

        Assert.Equal(labels, wide.DisplayLabels);                                   // 30 % of 1020 px holds them all, whole
        Assert.True(wide.LabelRight - TimelineStrip.PadX <= 0.30f * 1020f + 1f);
        Assert.True(narrow.LabelRight - TimelineStrip.PadX <= 0.30f * 340f + 1f);   // never more than ~30 %
        Assert.True(shortLabels.LabelRight < wide.LabelRight, "short labels do not waste the width");
        Assert.True(shortLabels.TrackRight - shortLabels.TrackLeft > wide.TrackRight - wide.TrackLeft);
    }

    [Fact]
    public void InANarrowStrip_TheSharedPrefixLabelsAreShortenedInTheMiddle_AndStillDiffer()
    {
        TimelineStrip.Layout narrow = Layout(340f, "Max 20x (alex@example.com)", "Max 20x (alex@example.org)", "Team (sam@example.net)");

        Assert.Equal(3, narrow.DisplayLabels.Distinct().Count());
        Assert.Contains("…", narrow.DisplayLabels[0]);
        Assert.EndsWith(".com)", narrow.DisplayLabels[0]);
        Assert.EndsWith(".org)", narrow.DisplayLabels[1]);
    }

    // ---- the popover's placement ----

    static readonly RectangleF Bounds = new(1, 1, 1018, 640);
    static readonly SizeF Popover = new(210, 36);

    static bool Inside(RectangleF box, RectangleF bounds) =>
        box.Left >= bounds.Left && box.Right <= bounds.Right && box.Top >= bounds.Top && box.Bottom <= bounds.Bottom;

    [Theory]
    [InlineData(100f, 60f)]     // a row near the top, left
    [InlineData(500f, 76f)]
    [InlineData(900f, 92f)]
    [InlineData(1010f, 108f)]   // the last row, at the right edge
    [InlineData(4f, 60f)]       // the very left edge
    public void ThePopover_NeverCoversTheHoveredSegment_AndStaysInsideTheContainer(float x, float y)
    {
        var segment = new RectangleF(Math.Max(1, x - 30), y, 60, 13);
        RectangleF box = TimelineStrip.PlacePopover(segment, new PointF(x, y + 6), Popover, Bounds);

        Assert.False(box.IntersectsWith(segment), $"{box} covers {segment}");
        Assert.True(Inside(box, Bounds), $"{box} leaves {Bounds}");
    }

    [Fact]
    public void ThePopover_FlipsToTheLeftOfTheMouse_NearTheRightEdge()
    {
        var segment = new RectangleF(950, 60, 60, 13);

        RectangleF box = TimelineStrip.PlacePopover(segment, new PointF(1000, 66), Popover, Bounds);

        Assert.True(box.Right <= Bounds.Right - 4f);
        Assert.True(box.Left < 1000f, "it sits to the left of the mouse rather than running off the edge");
    }

    [Fact]
    public void ThePopover_GoesAbove_WhenThereIsNoRoomBelow()
    {
        var shortBounds = new RectangleF(1, 1, 1018, 100);
        var segment = new RectangleF(300, 80, 60, 13);          // near the bottom of the visible part

        RectangleF box = TimelineStrip.PlacePopover(segment, new PointF(330, 86), Popover, shortBounds);

        Assert.True(box.Bottom <= segment.Top, $"{box} is not above {segment}");
        Assert.True(Inside(box, shortBounds));
    }

    [Fact]
    public void ThePopover_InANarrowCardsPanel_StaysInsideItsWidth()
    {
        var narrow = new RectangleF(1, 0, 338, 400);
        foreach (float x in new[] { 20f, 150f, 320f })
        {
            var segment = new RectangleF(Math.Max(1, x - 20), 70, 40, 13);
            RectangleF box = TimelineStrip.PlacePopover(segment, new PointF(x, 76), new SizeF(240, 34), narrow);

            Assert.True(Inside(box, narrow), $"{box} at x={x}");
            Assert.False(box.IntersectsWith(segment));
        }
    }

    // ---- over every row of a real strip ----

    [Fact]
    public void ThePopover_OverEverySegmentOfEveryRow_AtBothEdges_NeverCoversItAndStaysInside()
    {
        using var panel = new PanelForm();
        var views = new TimelineViewFactory(DateTimeOffset.UtcNow);
        TimeSpan h = TimeSpan.FromHours(1);
        panel.UpdateFull(new[]
        {
            new PanelModel("a", views.WeeklyRunsOut(4 * h, TimeSpan.FromDays(3)), "Max 20x (alex@example.com)", null, Array.Empty<OtherAccountRow>(), false, null, false),
            new PanelModel("b", views.WeeklyRunsOut(6 * h, 10 * h), "Max 20x (alex@example.org)", null, Array.Empty<OtherAccountRow>(), false, null, false),
            new PanelModel("c", views.SpentWeekly(30 * h), "Team (sam@example.net)", null, Array.Empty<OtherAccountRow>(), false, null, false),
        }, "a", false);

        AvailabilityTimeline timeline = panel.StripTimelineForTest!;
        TimelineStrip.Layout layout = panel.StripLayoutForTest!;
        var bounds = new RectangleF(1, 1, layout.Width - 2, panel.StripHeightForTest + 500f);

        int checks = 0;
        for (int account = -1; account < timeline.Accounts.Count; account++)
        {
            int count = account >= 0 ? timeline.Accounts[account].Segments.Count : timeline.Coverage.Count;
            for (int segment = 0; segment < count; segment++)
                foreach (float along in new[] { 0.02f, 0.5f, 0.98f })
                {
                    RectangleF rect = TimelineStrip.RectOf(layout, timeline, new TimelineHit(account, segment));
                    var mouse = new PointF(rect.X + rect.Width * along, rect.Y + rect.Height / 2f);

                    RectangleF box = TimelineStrip.PlacePopover(rect, mouse, Popover, bounds);

                    Assert.False(box.IntersectsWith(rect), $"row {account} segment {segment} along {along}: {box} covers {rect}");
                    Assert.True(Inside(box, bounds), $"row {account} segment {segment} along {along}: {box} leaves {bounds}");
                    checks++;
                }
        }
        Assert.True(checks >= 15);
    }

    // ---- what is painted ----

    [Fact]
    public void ThePopover_IsOpaque_AndPaintedOverTheColumnsText()
    {
        using var panel = new PanelForm();
        var views = new TimelineViewFactory(DateTimeOffset.UtcNow);
        TimeSpan h = TimeSpan.FromHours(1);
        panel.UpdateFull(new[]
        {
            new PanelModel("a", views.WeeklyRunsOut(4 * h, TimeSpan.FromDays(3)), "Personligt", "Max", Array.Empty<OtherAccountRow>(), false, null, false),
            new PanelModel("b", views.WeeklyRunsOut(6 * h, 10 * h), "Team", "Acme", Array.Empty<OtherAccountRow>(), false, null, false),
            new PanelModel("c", views.SpentWeekly(30 * h), "Enterprise", "Globex", Array.Empty<OtherAccountRow>(), false, null, false),
        }, "b", false);
        panel.SetStripHoverForDemo((-1, 1, 0.5f));        // the coverage row: the popover lands over the columns' header text
        var size = new Size((int)panel.StripWidthForTest, (int)(panel.StripHeightForTest + panel.FullPanelHeights[0]));

        using Bitmap painted = panel.PaintForTest(size);

        RectangleF box = panel.LastPopoverBoxForTest ?? throw new Xunit.Sdk.XunitException("no popover was painted");
        Assert.True(box.Bottom > panel.StripHeightForTest, "the popover reaches into the columns' area (the header text), which is the case that mattered");
        // Left padding of the box, top to bottom: between its border and its text, the fill must be EXACTLY the popover colour:
        // nothing of the panel or of the columns shows through, so nothing was painted over it afterwards either.
        for (int y = (int)box.Y + 8; y < (int)box.Bottom - 8; y += 3)
            Assert.Equal(TimelineStrip.PopoverFill, painted.GetPixel((int)box.X + 4, y));
        Assert.Equal(TimelineStrip.PopoverFill, painted.GetPixel((int)(box.X + box.Width / 2), (int)box.Bottom - 2));
    }
}

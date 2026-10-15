using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>What the timeline strip needs about one account: the short label on the left and its quota view.</summary>
public sealed record TimelineAccount(string Label, QuotaView View);

/// <summary>A segment under the mouse: Account is the row (-1 for the coverage row), Segment the index in that row's list.</summary>
internal readonly record struct TimelineHit(int Account, int Segment);

/// <summary>The fonts the strip draws with (PanelForm's own, so it reads like the rest of the panel).</summary>
internal sealed record TimelineFonts(Font Answer, Font Label, Font Small, Font Caption);

/// <summary>
/// The availability timeline strip (docs/multi-account.md "Availability timeline"): the one-line answer, the horizon
/// chips, an hour axis, one row per account and a coverage row. Layout and painting only -- the model is
/// Model/AvailabilityTimeline and the words are Ui/TimelineText. Everything is in the strip's OWN coordinates
/// (0,0 = its top-left corner); PanelForm translates it into place.
/// </summary>
internal static class TimelineStrip
{
    internal delegate float FitText(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, bool draw);

    public const float PadX = 14f;
    const float PadTop = 10f, AnswerGap = 6f, ChipsHeight = 16f, ChipsGap = 5f, AxisHeight = 12f, RowHeight = 13f, RowGap = 3f, CoverageGap = 5f, PadBottom = 9f;
    const float ChipWidth = 34f, ChipSpacing = 4f;

    // calm, dark-theme colours: muted green / muted red / grey, never the loud state colours
    static readonly Color AvailableColor = Color.FromArgb(255, 0x2B, 0x6E, 0x52);
    static readonly Color BlockedColor = Color.FromArgb(255, 0x93, 0x3B, 0x33);
    static readonly Color UnknownColor = Color.FromArgb(255, 0x58, 0x58, 0x58);
    static readonly Color GapColor = Color.FromArgb(255, 0xC8, 0x48, 0x3A);
    static readonly Color TrackColor = Color.FromArgb(22, 255, 255, 255);
    static readonly Color TickColor = Color.FromArgb(26, 255, 255, 255);
    static readonly Color NowColor = Color.FromArgb(150, 255, 255, 255);
    static readonly Color TextPrimary = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    static readonly Color TextSecondary = Color.FromArgb(255, 0x9A, 0x9A, 0x9A);
    static readonly Color TextTertiary = Color.FromArgb(255, 0x70, 0x70, 0x70);
    static readonly Color GapText = Color.FromArgb(255, 0xE5, 0x73, 0x65);
    static readonly Color ChipOn = Color.FromArgb(255, 0x4A, 0x4A, 0x4A);
    static readonly Color ChipOff = Color.FromArgb(255, 0x2E, 0x2E, 0x2E);
    // the hover popover: fully opaque (it is painted over the columns' own text), a touch lighter than the panel, with a border and a shadow
    static readonly Color PopoverBack = Color.FromArgb(255, 0x36, 0x36, 0x36);
    static readonly Color PopoverBorder = Color.FromArgb(255, 0x78, 0x78, 0x78);
    static readonly Color PopoverShadow = Color.FromArgb(110, 0, 0, 0);
    static readonly Color PopoverSecondary = Color.FromArgb(255, 0xC4, 0xC4, 0xC4);

    /// <summary>Where everything of one strip sits, for painting and hit-testing.</summary>
    internal sealed record Layout(
        float Width, float Height, float AnswerHeight,
        RectangleF CaptionRect, IReadOnlyList<(RectangleF Rect, TimelineHorizon Horizon)> Chips,
        float AxisY, float LabelRight, float TrackLeft, float TrackRight,
        IReadOnlyList<RectangleF> Rows, RectangleF CoverageRow, IReadOnlyList<string> DisplayLabels);

    /// <summary>The widest the label column may be, of the strip: wider labels are shortened in the middle (TimelineText.FitLabels).</summary>
    const float LabelColumnCap = 0.30f;

    /// <summary>
    /// The strip's size for these accounts at this width: the answer may wrap, so it is measured with the same fitter that
    /// draws it, and the label column is as wide as the widest label up to a third of the strip.
    /// </summary>
    public static Layout Measure(Graphics g, float width, IReadOnlyList<string> labels, string answer, TimelineFonts fonts, FitText fit)
    {
        int accountCount = labels.Count;
        float y = PadTop;
        float answerHeight = Math.Max(fonts.Answer.Height, fit(g, answer, fonts.Answer, TextPrimary, PadX, y, width - 2 * PadX, false));
        y += answerHeight + AnswerGap;

        // the caption and the chips share one row, at the LEFT (a row wider than the screen scrolls: the chips stay reachable)
        var caption = new RectangleF(PadX, y, 96f, ChipsHeight);
        var chips = new List<(RectangleF, TimelineHorizon)>();
        float cx = caption.Right + 6f;
        foreach (TimelineHorizon h in new[] { TimelineHorizon.Hours24, TimelineHorizon.Days3, TimelineHorizon.Days7 })
        {
            chips.Add((new RectangleF(cx, y, ChipWidth, ChipsHeight), h));
            cx += ChipWidth + ChipSpacing;
        }
        y += ChipsHeight + ChipsGap;

        float axisY = y;
        y += AxisHeight + 2f;

        float widest = Math.Max(g.MeasureString(TimelineText.CoverageLabel, fonts.Label).Width, labels.Count == 0 ? 0f : labels.Max(s => g.MeasureString(s, fonts.Label, int.MaxValue, StringFormat.GenericTypographic).Width)) + 6f;
        float labelWidth = Math.Clamp(widest, 60f, Math.Max(60f, width * LabelColumnCap));
        IReadOnlyList<string> display = TimelineText.FitLabels(labels, s => g.MeasureString(s, fonts.Label, int.MaxValue, StringFormat.GenericTypographic).Width + 2f, labelWidth - 4f);
        float labelRight = PadX + labelWidth;
        float trackLeft = labelRight + 8f, trackRight = width - PadX;

        var rows = new List<RectangleF>();
        for (int i = 0; i < accountCount; i++)
        {
            rows.Add(new RectangleF(trackLeft, y, trackRight - trackLeft, RowHeight));
            y += RowHeight + RowGap;
        }
        y += CoverageGap - RowGap;
        var coverage = new RectangleF(trackLeft, y, trackRight - trackLeft, RowHeight);
        y += RowHeight + PadBottom;

        return new Layout(width, y, answerHeight, caption, chips, axisY, labelRight, trackLeft, trackRight, rows, coverage, display);
    }

    static float X(Layout l, AvailabilityTimeline t, DateTimeOffset at)
    {
        double frac = (at - t.Now).TotalSeconds / Math.Max(1.0, (t.HorizonEnd - t.Now).TotalSeconds);
        return l.TrackLeft + (float)Math.Clamp(frac, 0.0, 1.0) * (l.TrackRight - l.TrackLeft);
    }

    static RectangleF SegmentRect(Layout l, AvailabilityTimeline t, RectangleF row, DateTimeOffset start, DateTimeOffset end)
    {
        float x0 = X(l, t, start), x1 = X(l, t, end);
        return new RectangleF(x0, row.Y, Math.Max(1.5f, x1 - x0), row.Height);
    }

    // ---- hit testing ----

    public static TimelineHorizon? ChipAt(Layout l, PointF p)
    {
        foreach ((RectangleF rect, TimelineHorizon h) in l.Chips)
            if (rect.Contains(p)) return h;
        return null;
    }

    public static TimelineHit? SegmentAt(Layout l, AvailabilityTimeline t, PointF p)
    {
        for (int a = 0; a < l.Rows.Count && a < t.Accounts.Count; a++)
        {
            if (p.Y < l.Rows[a].Y || p.Y > l.Rows[a].Bottom) continue;
            IReadOnlyList<AvailabilitySegment> segs = t.Accounts[a].Segments;
            for (int s = 0; s < segs.Count; s++)
                if (SegmentRect(l, t, l.Rows[a], segs[s].Start, segs[s].End).Contains(p)) return new TimelineHit(a, s);
        }
        if (p.Y >= l.CoverageRow.Y && p.Y <= l.CoverageRow.Bottom)
            for (int c = 0; c < t.Coverage.Count; c++)
                if (SegmentRect(l, t, l.CoverageRow, t.Coverage[c].Start, t.Coverage[c].End).Contains(p)) return new TimelineHit(-1, c);
        return null;
    }

    // ---- painting ----

    public static void Draw(
        Graphics g, Layout l, AvailabilityTimeline t, TimelineHorizon horizon, string answer,
        TimelineHit? hover, PointF mouse, TimeZoneInfo tz, TimelineFonts fonts, FitText fit)
    {
        SmoothingMode smoothing = g.SmoothingMode;

        // the answer first
        bool hasGap = t.Gaps.Count > 0;
        fit(g, answer, fonts.Answer, hasGap ? GapText : TextPrimary, PadX, PadTop, l.Width - 2 * PadX, true);

        // caption and chips
        using (var captionBrush = new SolidBrush(TextTertiary))
        using (var captionFmt = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(TimelineText.Title, fonts.Caption, captionBrush, l.CaptionRect, captionFmt);
        foreach ((RectangleF rect, TimelineHorizon h) in l.Chips)
        {
            bool on = h == horizon;
            using (var path = RoundedRect(rect, 4f))
            using (var back = new SolidBrush(on ? ChipOn : ChipOff))
                g.FillPath(back, path);
            using var textBrush = new SolidBrush(on ? TextPrimary : TextSecondary);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(h.Label(), fonts.Small, textBrush, rect, fmt);
        }

        // rows (tracks first, then ticks over them, then segments, so a tick never cuts a segment's colour)
        float rowsTop = l.Rows.Count > 0 ? l.Rows[0].Y : l.CoverageRow.Y;
        float rowsBottom = l.CoverageRow.Bottom;
        g.SmoothingMode = SmoothingMode.None;
        using (var track = new SolidBrush(TrackColor))
        {
            foreach (RectangleF row in l.Rows) g.FillRectangle(track, row);
            g.FillRectangle(track, l.CoverageRow);
        }

        // axis: ticks with day-aware labels, "nu" at the left edge
        using (var tickPen = new Pen(TickColor))
        using (var labelBrush = new SolidBrush(TextTertiary))
        {
            float lastLabelRight = l.TrackLeft + 16f;   // keeps clear of the "nu"
            foreach (TimelineText.Tick tick in TimelineText.Ticks(t.Now, t.HorizonEnd, tz))
            {
                float x = X(l, t, tick.At);
                g.DrawLine(tickPen, x, rowsTop - 2f, x, rowsBottom);
                if (tick.Label.Length == 0) continue;
                SizeF size = g.MeasureString(tick.Label, fonts.Small);
                float lx = Math.Clamp(x - size.Width / 2f, l.TrackLeft, l.TrackRight - size.Width);
                if (lx < lastLabelRight + 3f) continue;
                g.DrawString(tick.Label, fonts.Small, labelBrush, lx, l.AxisY - 1f);
                lastLabelRight = lx + size.Width;
            }
        }

        // account rows
        g.SmoothingMode = SmoothingMode.None;
        for (int a = 0; a < l.Rows.Count && a < t.Accounts.Count; a++)
        {
            using (var labelBrush = new SolidBrush(TextSecondary))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, LineAlignment = StringAlignment.Center })
                g.DrawString(l.DisplayLabels[a], fonts.Label, labelBrush, new RectangleF(PadX, l.Rows[a].Y - 1f, l.LabelRight - PadX, l.Rows[a].Height + 2f), fmt);

            IReadOnlyList<AvailabilitySegment> segs = t.Accounts[a].Segments;
            for (int s = 0; s < segs.Count; s++)
                DrawSegment(g, SegmentRect(l, t, l.Rows[a], segs[s].Start, segs[s].End), segs[s]);
        }

        // coverage row: gaps in red, covered stretches in green
        using (var labelBrush = new SolidBrush(TextSecondary))
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            g.DrawString(TimelineText.CoverageLabel, fonts.Label, labelBrush, new RectangleF(PadX, l.CoverageRow.Y - 1f, l.LabelRight - PadX, l.CoverageRow.Height + 2f), fmt);
        foreach (CoverageInterval c in t.Coverage)
        {
            RectangleF rect = SegmentRect(l, t, l.CoverageRow, c.Start, c.End);
            Color color = c.Available.Count == 0 ? GapColor : Color.FromArgb(c.Available.Count == 1 ? 120 : 190, AvailableColor);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, rect);
        }

        // "nu"
        using (var nowPen = new Pen(NowColor))
            g.DrawLine(nowPen, l.TrackLeft, l.AxisY + 1f, l.TrackLeft, rowsBottom);
        using (var nowBrush = new SolidBrush(TextSecondary))
            g.DrawString(TimelineText.NowLabel, fonts.Small, nowBrush, l.TrackLeft + 2f, l.AxisY - 1f);

        // the hovered segment, outlined (its label is the popover, drawn LAST by the caller: DrawPopover)
        if (hover is { } h2)
        {
            RectangleF rect = RectOf(l, t, h2);
            using var outline = new Pen(Color.FromArgb(220, 255, 255, 255));
            g.DrawRectangle(outline, rect.X, rect.Y, rect.Width - 1f, rect.Height - 1f);
        }
        g.SmoothingMode = smoothing;
    }

    internal static RectangleF RectOf(Layout l, AvailabilityTimeline t, TimelineHit h) => h.Account >= 0
        ? SegmentRect(l, t, l.Rows[h.Account], t.Accounts[h.Account].Segments[h.Segment].Start, t.Accounts[h.Account].Segments[h.Segment].End)
        : SegmentRect(l, t, l.CoverageRow, t.Coverage[h.Segment].Start, t.Coverage[h.Segment].End);

    /// <summary>
    /// One segment: its state colour, calm; an estimate (a forecast run-out) is marked with a diagonal hatch over its
    /// uncertain edge -- the end of an "available" stretch, the start of a "blocked" one; "no forecast after a reset"
    /// is the same green, softer.
    /// </summary>
    static void DrawSegment(Graphics g, RectangleF rect, AvailabilitySegment s)
    {
        Color color = s.Kind switch
        {
            AvailabilityKind.Blocked => BlockedColor,
            AvailabilityKind.Unknown => UnknownColor,
            _ => s.Reason == AvailabilityReason.NoForecastAfterReset ? Color.FromArgb(110, AvailableColor) : AvailableColor,
        };
        using (var fill = new SolidBrush(color)) g.FillRectangle(fill, rect);

        if (!s.IsForecast) return;
        float hatchWidth = Math.Min(rect.Width, Math.Max(10f, Math.Min(22f, rect.Width * 0.5f)));
        RectangleF hatch = s.Kind == AvailabilityKind.Available
            ? new RectangleF(rect.Right - hatchWidth, rect.Y, hatchWidth, rect.Height)
            : new RectangleF(rect.X, rect.Y, hatchWidth, rect.Height);
        using var brush = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 0, 0, 0));
        g.FillRectangle(brush, hatch);
    }

    /// <summary>
    /// Where the hover popover goes (all in the strip's coordinates): a pure function so every row, both edges and the
    /// cards view can be tested. It is never ON the hovered segment: below it when there is room (the usual case -- the
    /// strip is at the top), above it when there is not; sideways it follows the mouse, flipping to the left of it near the
    /// right edge, and is clamped inside `bounds` (the visible part of the container).
    /// </summary>
    internal static RectangleF PlacePopover(RectangleF segment, PointF mouse, SizeF size, RectangleF bounds)
    {
        const float gap = 6f, margin = 4f;

        float x = mouse.X + 10f;
        if (x + size.Width > bounds.Right - margin) x = mouse.X - 10f - size.Width;
        x = Math.Clamp(x, bounds.Left + margin, Math.Max(bounds.Left + margin, bounds.Right - margin - size.Width));

        float below = segment.Bottom + gap;
        float above = segment.Top - gap - size.Height;
        bool fitsBelow = below + size.Height <= bounds.Bottom - margin;
        bool fitsAbove = above >= bounds.Top + margin;

        float y;
        if (fitsBelow) y = below;
        else if (fitsAbove) y = above;
        else y = bounds.Bottom - below >= segment.Top - bounds.Top
            ? Math.Min(below, Math.Max(bounds.Top + margin, bounds.Bottom - margin - size.Height))   // neither fits: the roomier side, kept inside
            : Math.Max(bounds.Top + margin, above);
        return new RectangleF(x, y, size.Width, size.Height);
    }

    /// <summary>The two lines of a segment's popover: whose and from-to, and why.</summary>
    static (string Title, string Reason) PopoverText(AvailabilityTimeline t, TimelineHit h, TimeZoneInfo tz)
    {
        if (h.Account >= 0)
        {
            AvailabilitySegment seg = t.Accounts[h.Account].Segments[h.Segment];
            return (TimelineText.SegmentTitle(t.Accounts[h.Account].Label, seg, t.Now, tz), TimelineText.SegmentReason(seg, t.Now, tz));
        }
        CoverageInterval c = t.Coverage[h.Segment];
        return (TimelineText.CoverageTitle(c, t, tz), TimelineText.CoverageReason(c, t));
    }

    /// <summary>
    /// The hover label as a proper popover: fully opaque, a little lighter than the panel, a 1 px border and a small
    /// shadow, so nothing under it shows through. The caller paints it LAST (after the columns / the cards and the
    /// header), so nothing draws over it. `bounds` is the visible container in the strip's coordinates.
    /// </summary>
    public static RectangleF DrawPopover(Graphics g, Layout l, AvailabilityTimeline t, TimelineHit h, PointF mouse, RectangleF bounds, TimeZoneInfo tz, TimelineFonts fonts)
    {
        (string title, string reason) = PopoverText(t, h, tz);

        const float padX = 8f, padY = 5f;
        int maxTextWidth = (int)Math.Max(80f, bounds.Width - 2 * 4f - 2 * padX);
        SizeF titleSize = g.MeasureString(title, fonts.Small, maxTextWidth), reasonSize = g.MeasureString(reason, fonts.Small, maxTextWidth);
        var size = new SizeF(Math.Min(maxTextWidth, Math.Max(titleSize.Width, reasonSize.Width)) + 2 * padX, titleSize.Height + reasonSize.Height + 2 * padY + 1f);

        RectangleF box = PlacePopover(RectOf(l, t, h), mouse, size, bounds);

        SmoothingMode smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var shadowBox = new RectangleF(box.X + 1f, box.Y + 2.5f, box.Width, box.Height);
        using (var shadowPath = RoundedRect(shadowBox, 5f))
        using (var shadow = new SolidBrush(PopoverShadow))
            g.FillPath(shadow, shadowPath);
        using (var path = RoundedRect(box, 4f))
        using (var back = new SolidBrush(PopoverBack))
        using (var border = new Pen(PopoverBorder, 1f))
        {
            g.FillPath(back, path);
            g.DrawPath(border, path);
        }
        g.SmoothingMode = smoothing;

        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(PopoverSecondary);
        g.DrawString(title, fonts.Small, primary, new RectangleF(box.X + padX, box.Y + padY, box.Width - 2 * padX, titleSize.Height));
        g.DrawString(reason, fonts.Small, secondary, new RectangleF(box.X + padX, box.Y + padY + titleSize.Height + 1f, box.Width - 2 * padX, reasonSize.Height));
        return box;
    }

    /// <summary>The popover's fill colour (tests: it must be what shows, whatever is underneath).</summary>
    internal static Color PopoverFill => PopoverBack;

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

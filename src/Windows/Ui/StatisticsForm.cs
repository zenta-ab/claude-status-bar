using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// docs/statistics.md decision 4, the task's item 1: a normal top-level window (unlike PanelForm,
/// this one has a title bar and may take focus) reached from the tray menu's "Statistik...". Dark
/// theme and typography match PanelForm exactly (same colours, same two font families, same
/// DrawFitText width-guard-then-wrap algorithm); header navigation (account picker, the three
/// period presets) uses plain WinForms controls, while every actual statistic -- tiles,
/// recommendations, recent cycles with their sparklines, the calendar heatmap -- is hand-painted
/// in OnPaint, same "no chart library" discipline the panel already follows. All wording comes
/// from Ui/StatisticsText.Compose (pure); this class only lays it out and paints it.
/// </summary>
public sealed class StatisticsForm : Form
{
    const int LogicalWidth = 640;
    const float SidePadding = 18f;
    const float CardGap = 12f;
    const float TileCardHeight = 74f;
    const float SparklineWidth = 90f;
    const float SparklineHeight = 22f;
    const float HeatCell = 13f;
    const float HeatGap = 3f;

    public readonly record struct AccountRef(string Label, string LogDir, string AccountKey);

    static readonly Color PanelBackground = Color.FromArgb(255, 0x20, 0x20, 0x20);
    static readonly Color CardBackground = Color.FromArgb(255, 0x27, 0x27, 0x27);
    static readonly Color BorderColor = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);
    static readonly Color RuleColor = Color.FromArgb(255, 0x33, 0x33, 0x33);
    static readonly Color TextPrimary = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    static readonly Color TextSecondary = Color.FromArgb(255, 0x9A, 0x9A, 0x9A);
    static readonly Color TextTertiary = Color.FromArgb(255, 0x70, 0x70, 0x70);
    static readonly Color HeatNoData = Color.FromArgb(255, 0x2C, 0x2C, 0x2C);
    static readonly Color HeatIncomplete = Color.FromArgb(255, 0x4A, 0x46, 0x2E); // Codex review #12: a distinct neutral "okänd" cell, never Normal's green or NoData's plain grey
    static readonly Color HeatNormal = Color.FromArgb(140, 0x12, 0xA2, 0x77);
    static readonly Color HeatCeiling = Color.FromArgb(220, 0xE2, 0x3D, 0x28);

    static readonly string DisplayFamily = ResolveFamily("Segoe UI Variable Display", "Segoe UI");
    static readonly string TextFamily = ResolveFamily("Segoe UI Variable Text", "Segoe UI");

    readonly Font _fontTitle = new(DisplayFamily, 18f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _fontSectionTitle = new(TextFamily, 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _fontTileTitle = new(TextFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _fontTileValue = new(DisplayFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _fontTileValueLearning = new(TextFamily, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _fontTileN = new(TextFamily, 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _fontBody = new(TextFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _fontSmall = new(TextFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    readonly Panel _header;
    readonly Label _titleLabel;
    readonly ComboBox? _accountCombo;
    readonly Button _btn2w;
    readonly Button _btn3m;
    readonly Button _btnYear;
    readonly ContentPanel _content;
    readonly ToolTip _heatmapTip = new();
    readonly Bitmap _measureBmp = new(1, 1);
    readonly Graphics _measureG;

    readonly IReadOnlyList<AccountRef> _accounts;
    readonly TimeZoneInfo _tz;
    int _accountIndex;
    StatisticsPeriod _period = StatisticsPeriod.TwoWeeks;

    StatisticsDataLoader.LoadedData? _data;
    StatisticsWindowText? _text;
    ContentLayout? _layout;
    string? _lastHeatmapTooltip;

    /// <summary>Test-only override so a capture/verification run can freeze "now" instead of racing DateTimeOffset.UtcNow.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    public StatisticsForm(IReadOnlyList<AccountRef> accounts, TimeZoneInfo tz)
    {
        _accounts = accounts.Count > 0 ? accounts : new[] { new AccountRef("Konto", ".", "_none") };
        _tz = tz;
        _measureG = Graphics.FromImage(_measureBmp);

        Text = "Statistik – Claude Code";
        BackColor = PanelBackground;
        ForeColor = TextPrimary;
        Font = _fontBody;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalWidth, 720);
        MinimumSize = new Size(LogicalWidth + 16, 400);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;

        _header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = PanelBackground };
        _titleLabel = new Label
        {
            Text = "Statistik", Font = _fontTitle, ForeColor = TextPrimary, AutoSize = true,
            BackColor = Color.Transparent, Location = new Point((int)SidePadding, 10),
        };
        _header.Controls.Add(_titleLabel);

        if (_accounts.Count > 1)
        {
            _accountCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed,
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBackground,
                ForeColor = TextPrimary,
                Font = _fontSmall,
                Width = 200,
                Location = new Point(LogicalWidth - (int)SidePadding - 200, 14),
            };
            foreach (AccountRef a in _accounts) _accountCombo.Items.Add(a.Label);
            _accountCombo.SelectedIndex = 0;
            _accountCombo.DrawItem += AccountCombo_DrawItem;
            _accountCombo.SelectedIndexChanged += (_, _) => { _accountIndex = _accountCombo.SelectedIndex; Reload(); };
            _header.Controls.Add(_accountCombo);
        }

        _btn2w = MakePeriodButton("2 veckor", (int)SidePadding, 46);
        _btn3m = MakePeriodButton("3 månader", 0, 46);
        _btnYear = MakePeriodButton("År", 0, 46);
        LayoutPeriodButtons();
        _btn2w.Click += (_, _) => SetPeriod(StatisticsPeriod.TwoWeeks);
        _btn3m.Click += (_, _) => SetPeriod(StatisticsPeriod.ThreeMonths);
        _btnYear.Click += (_, _) => SetPeriod(StatisticsPeriod.Year);
        _header.Controls.Add(_btn2w);
        _header.Controls.Add(_btn3m);
        _header.Controls.Add(_btnYear);

        _content = new ContentPanel(this) { Dock = DockStyle.Fill, BackColor = PanelBackground, AutoScroll = true };
        _content.MouseMove += Content_MouseMove;
        _content.MouseLeave += (_, _) => HideHeatmapTip();

        Controls.Add(_content);
        Controls.Add(_header);

        UpdatePeriodButtonStyles();
        Reload();
    }

    Button MakePeriodButton(string text, int x, int y) => new()
    {
        Text = text, FlatStyle = FlatStyle.Flat, Font = _fontSmall, ForeColor = TextPrimary,
        BackColor = CardBackground, Location = new Point(x, y), Size = new Size(0, 26), AutoSize = true,
        FlatAppearance = { BorderColor = BorderColor, BorderSize = 1 },
    };

    void LayoutPeriodButtons()
    {
        int x = (int)SidePadding;
        foreach (Button b in new[] { _btn2w, _btn3m, _btnYear })
        {
            b.Location = new Point(x, 46);
            x += b.Width + 8;
        }
    }

    void AccountCombo_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && e.Index < _accounts.Count)
        {
            using var brush = new SolidBrush(TextPrimary);
            using var bg = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? BorderColor : CardBackground);
            e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.DrawString(_accounts[e.Index].Label, e.Font ?? _fontSmall, brush, e.Bounds.Left + 4, e.Bounds.Top + 2);
        }
        e.DrawFocusRectangle();
    }

    void SetPeriod(StatisticsPeriod period)
    {
        if (_period == period) return;
        _period = period;
        UpdatePeriodButtonStyles();
        Reload();
    }

    void UpdatePeriodButtonStyles()
    {
        (Button Button, StatisticsPeriod Period)[] pairs =
        {
            (_btn2w, StatisticsPeriod.TwoWeeks), (_btn3m, StatisticsPeriod.ThreeMonths), (_btnYear, StatisticsPeriod.Year),
        };
        foreach ((Button b, StatisticsPeriod p) in pairs)
        {
            bool selected = p == _period;
            b.BackColor = selected ? Color.FromArgb(255, 0x12, 0xA2, 0x77) : CardBackground;
            b.ForeColor = selected ? Color.Black : TextPrimary;
        }
        LayoutPeriodButtons();
    }

    /// <summary>Test/capture-only: switches the account shown without needing a live ComboBox click.</summary>
    public void SelectAccount(int index)
    {
        if (index < 0 || index >= _accounts.Count) return;
        _accountIndex = index;
        if (_accountCombo != null) _accountCombo.SelectedIndex = index; // fires SelectedIndexChanged -> Reload()
        else Reload();
    }

    /// <summary>Test/capture-only: switches the period preset without needing a live button click.</summary>
    public void SelectPeriod(StatisticsPeriod period) => SetPeriod(period);

    /// <summary>
    /// The task's "capture" item: renders this window's FULL content -- not just whatever fits
    /// in the current window height -- into an in-memory Bitmap via DrawToBitmap, never
    /// CopyFromScreen (a locked/inactive session cannot be screen-captured at all, which is
    /// exactly why this path exists). Temporarily grows the window tall enough that
    /// AutoScrollMinSize's full content fits without a scrollbar, forces every child control's
    /// handle to exist (DrawToBitmap needs one, even for a window that was never Show()n), then
    /// restores nothing -- this is a capture-only, throwaway sizing, the form is expected to be
    /// discarded or Reload()ed/resized again by its caller afterward.
    /// </summary>
    public Bitmap CaptureFullContent()
    {
        int neededHeight = _header.Height + (int)Math.Ceiling(_layout?.TotalHeight ?? 200f) + 24;
        ClientSize = new Size(LogicalWidth, neededHeight);

        // DrawToBitmap only paints child controls (the header's title/buttons/combo, the
        // content panel's tiles/recommendations/heatmap) once the window has actually been
        // shown at least once -- CreateControl() alone (which forces handles without ever
        // showing) left every child entirely blank (confirmed by the first capture attempt
        // here). Moved off-screen first so this never visibly flashes over whatever else is on
        // the desktop; Show() itself does not depend on desktop compositing or an unlocked
        // session the way CopyFromScreen does, which is exactly why this capture path uses
        // DrawToBitmap instead (docs/statistics.md task item 3).
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Show();
        PerformLayout();
        _content.PerformLayout();
        _content.Invalidate();
        Update();

        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
        Hide();
        return bmp;
    }

    /// <summary>Reloads this account+period's data from disk and re-composes its text -- called on startup and whenever the account or period selection changes. Public so a capture/verification run can force a specific account/period deterministically before painting.</summary>
    public void Reload()
    {
        AccountRef account = _accounts[Math.Clamp(_accountIndex, 0, _accounts.Count - 1)];
        DateTimeOffset now = NowProvider();
        StatisticsDataLoader.LoadedData data = StatisticsDataLoader.Load(account.LogDir, account.AccountKey, _period, _tz, now);
        _data = data;
        _text = StatisticsText.Compose(data, _period, _tz, now);
        _layout = BuildLayout(_text);
        _content.AutoScrollMinSize = new Size(0, (int)Math.Ceiling(_layout.Value.TotalHeight));
        _content.Invalidate();
    }

    // ---- layout ----

    readonly record struct TileRect(RectangleF Rect, TileText Text);
    readonly record struct RecentRow(RectangleF RowRect, RectangleF SparkRect, RecentCycleText Text, IReadOnlyList<RecentCycleSparkline.Point> Points);
    readonly record struct HeatCellRect(RectangleF Rect, HeatmapDayText Day);

    readonly record struct ContentLayout(
        IReadOnlyList<TileRect> Tiles,
        RectangleF? CeilingRecRect, RectangleF? WeeklyRecRect,
        float RecentHeaderY, IReadOnlyList<RecentRow> RecentRows,
        float HeatmapHeaderY, IReadOnlyList<HeatCellRect> HeatCells,
        float TotalHeight);

    float ContentWidth => LogicalWidth - 2 * SidePadding;

    ContentLayout BuildLayout(StatisticsWindowText? text)
    {
        if (text is null) return new ContentLayout(Array.Empty<TileRect>(), null, null, 0, Array.Empty<RecentRow>(), 0, Array.Empty<HeatCellRect>(), 20f);

        float y = 16f;

        // Tiles: 2-column grid.
        var tiles = new List<TileRect>();
        const int cols = 2;
        float tileWidth = (ContentWidth - CardGap) / cols;
        for (int i = 0; i < text.Tiles.Count; i++)
        {
            int row = i / cols, col = i % cols;
            var rect = new RectangleF(SidePadding + col * (tileWidth + CardGap), y + row * (TileCardHeight + CardGap), tileWidth, TileCardHeight);
            tiles.Add(new TileRect(rect, text.Tiles[i]));
        }
        int rows = (text.Tiles.Count + cols - 1) / cols;
        y += rows * (TileCardHeight + CardGap) + 10f;

        RectangleF? ceilingRect = null;
        if (text.CeilingRecommendation is not null)
        {
            ceilingRect = new RectangleF(SidePadding, y, ContentWidth, 56f);
            y += 56f + CardGap;
        }
        RectangleF? weeklyRect = null;
        if (text.WeeklyBudgetRecommendation is not null)
        {
            weeklyRect = new RectangleF(SidePadding, y, ContentWidth, 56f);
            y += 56f + CardGap;
        }
        y += 8f;

        float recentHeaderY = y;
        y += DrawFitText(_measureG, "SENASTE CYKLARNA", _fontSectionTitle, TextTertiary, 0, 0, ContentWidth, draw: false) + 8f;

        var recentRows = new List<RecentRow>();
        if (text.RecentCycles.Count == 0)
        {
            y += DrawFitText(_measureG, "Inga cykler ännu.", _fontSmall, TextSecondary, 0, 0, ContentWidth, draw: false) + 8f;
        }
        else
        {
            IReadOnlyList<StatisticsDataLoader.RecentCycle> rawRecent = _data?.RecentCycles ?? Array.Empty<StatisticsDataLoader.RecentCycle>();
            for (int i = 0; i < text.RecentCycles.Count; i++)
            {
                RecentCycleText rc = text.RecentCycles[i];
                float rowTop = y;
                float rowHeight = Math.Max(SparklineHeight + 4f,
                    DrawFitText(_measureG, rc.RangeLine, _fontSmall, TextSecondary, 0, 0, ContentWidth - SparklineWidth - 12f, draw: false)
                    + DrawFitText(_measureG, rc.PeakLine, _fontSmall, TextSecondary, 0, 0, ContentWidth - SparklineWidth - 12f, draw: false)
                    + (rc.Incomplete ? DrawFitText(_measureG, rc.IncompleteLine ?? "", _fontSmall, Palette.Tight, 0, 0, ContentWidth - SparklineWidth - 12f, draw: false) : 0f));
                var rowRect = new RectangleF(SidePadding, rowTop, ContentWidth, rowHeight);
                var sparkRect = new RectangleF(LogicalWidth - SidePadding - SparklineWidth, rowTop + (rowHeight - SparklineHeight) / 2f, SparklineWidth, SparklineHeight);
                IReadOnlyList<RecentCycleSparkline.Point> points = i < rawRecent.Count ? rawRecent[i].Sparkline : Array.Empty<RecentCycleSparkline.Point>();
                recentRows.Add(new RecentRow(rowRect, sparkRect, rc, points));
                y += rowHeight + 10f;
            }
        }
        y += 10f;

        float heatmapHeaderY = y;
        y += DrawFitText(_measureG, "AKTIVITET", _fontSectionTitle, TextTertiary, 0, 0, ContentWidth, draw: false) + 8f;

        var heatCells = new List<HeatCellRect>();
        if (text.Heatmap.Count > 0)
        {
            // GitHub-style: one column per ISO week, one row per weekday (Monday=row 0 .. Sunday=row 6).
            DateOnly first = text.Heatmap[0].LocalDate;
            int firstRow = ((int)first.DayOfWeek + 6) % 7;
            foreach (HeatmapDayText day in text.Heatmap)
            {
                int daysSinceFirst = day.LocalDate.DayNumber - first.DayNumber;
                int row = ((int)day.LocalDate.DayOfWeek + 6) % 7;
                int col = (daysSinceFirst + firstRow) / 7;
                var rect = new RectangleF(SidePadding + col * (HeatCell + HeatGap), y + row * (HeatCell + HeatGap), HeatCell, HeatCell);
                heatCells.Add(new HeatCellRect(rect, day));
            }
            y += 7 * (HeatCell + HeatGap) + 16f;
        }
        else
        {
            y += DrawFitText(_measureG, "Ingen data för perioden.", _fontSmall, TextSecondary, 0, 0, ContentWidth, draw: false) + 8f;
        }

        y += 20f;
        return new ContentLayout(tiles, ceilingRect, weeklyRect, recentHeaderY, recentRows, heatmapHeaderY, heatCells, y);
    }

    // ---- paint ----

    void PaintContent(Graphics g)
    {
        g.Clear(PanelBackground);
        if (_layout is not { } layout || _text is not { } text) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        foreach (TileRect t in layout.Tiles) DrawTile(g, t);
        if (layout.CeilingRecRect is { } cr && text.CeilingRecommendation is { } ceilingText) DrawRecommendation(g, cr, ceilingText);
        if (layout.WeeklyRecRect is { } wr && text.WeeklyBudgetRecommendation is { } weeklyText) DrawRecommendation(g, wr, weeklyText);

        DrawFitText(g, "SENASTE CYKLARNA", _fontSectionTitle, TextTertiary, SidePadding, layout.RecentHeaderY, ContentWidth);
        if (layout.RecentRows.Count == 0 && text.RecentCycles.Count == 0)
        {
            DrawFitText(g, "Inga cykler ännu.", _fontSmall, TextSecondary, SidePadding, layout.RecentHeaderY + 24f, ContentWidth);
        }
        foreach (RecentRow row in layout.RecentRows) DrawRecentRow(g, row);

        DrawFitText(g, "AKTIVITET", _fontSectionTitle, TextTertiary, SidePadding, layout.HeatmapHeaderY, ContentWidth);
        if (layout.HeatCells.Count == 0)
        {
            DrawFitText(g, "Ingen data för perioden.", _fontSmall, TextSecondary, SidePadding, layout.HeatmapHeaderY + 24f, ContentWidth);
        }
        foreach (HeatCellRect cell in layout.HeatCells) DrawHeatCell(g, cell);
    }

    void DrawTile(Graphics g, TileRect t)
    {
        using (var path = RoundedRect(t.Rect, 8f))
        using (var bg = new SolidBrush(CardBackground))
            g.FillPath(bg, path);

        float x = t.Rect.X + 12f;
        float innerWidth = t.Rect.Width - 24f;
        float y = t.Rect.Y + 10f;
        y += DrawFitText(g, t.Text.Title, _fontTileTitle, TextSecondary, x, y, innerWidth) + 4f;

        Font valueFont = t.Text.Ready ? _fontTileValue : _fontTileValueLearning;
        Color valueColor = t.Text.Ready ? TextPrimary : TextTertiary;
        y += DrawFitText(g, t.Text.ValueLine, valueFont, valueColor, x, y, innerWidth) + 2f;

        DrawFitText(g, t.Text.NLine, _fontTileN, TextTertiary, x, t.Rect.Bottom - 18f, innerWidth);
    }

    void DrawRecommendation(Graphics g, RectangleF rect, RecommendationText rec)
    {
        Color accent = Color.FromArgb(255, 0x12, 0xA2, 0x77);
        using (var path = RoundedRect(rect, 8f))
        using (var bg = new SolidBrush(Color.FromArgb(24, accent)))
            g.FillPath(bg, path);
        using (var edge = new SolidBrush(accent))
            g.FillRectangle(edge, rect.X, rect.Y, 3f, rect.Height);

        float x = rect.X + 14f;
        float innerWidth = rect.Width - 28f;
        float y = rect.Y + 8f;
        y += DrawFitText(g, rec.Line, _fontBody, TextPrimary, x, y, innerWidth) + 2f;
        DrawFitText(g, rec.NLine, _fontTileN, TextTertiary, x, y, innerWidth);
    }

    void DrawRecentRow(Graphics g, RecentRow row)
    {
        float x = row.RowRect.X;
        float rightWidth = ContentWidth - SparklineWidth - 12f;
        float y = row.RowRect.Y;

        string marker = row.Text.HitCeiling ? "  ●" : "";
        Color markerColor = Palette.Crit;
        string header = $"{row.Text.RangeLine}  ·  {row.Text.KindLabel}";
        y += DrawFitText(g, header, _fontSmall, TextPrimary, x, y, rightWidth) + 2f;
        if (row.Text.HitCeiling)
            DrawFitText(g, marker.Trim(), _fontSmall, markerColor, x + row.RowRect.Width - SparklineWidth - 30f, row.RowRect.Y, 20f);

        y += DrawFitText(g, row.Text.PeakLine, _fontSmall, TextSecondary, x, y, rightWidth) + 2f;
        if (row.Text.Incomplete && row.Text.IncompleteLine is { } incLine)
            DrawFitText(g, incLine, _fontSmall, Palette.Tight, x, y, rightWidth);

        DrawSparkline(g, row.SparkRect, row.Points);
    }

    void DrawSparkline(Graphics g, RectangleF rect, IReadOnlyList<RecentCycleSparkline.Point> points)
    {
        using (var track = new Pen(BorderColor, 1f))
            g.DrawRectangle(track, rect.X, rect.Y, rect.Width, rect.Height);

        if (points.Count < 2)
        {
            // Honestly "no shape data" (the raw window-shape-*.csv rows this cycle would need
            // are past the 60-day retention, or -- a demo account -- were never written at all)
            // -- a faint centred dash instead of a silently empty box, so it reads as "no data"
            // rather than as a rendering bug.
            using var dash = new Pen(TextTertiary, 1f) { DashStyle = DashStyle.Dash };
            float midY = rect.Y + rect.Height / 2f;
            g.DrawLine(dash, rect.X + 4f, midY, rect.Right - 4f, midY);
            return;
        }

        using var pen = new Pen(Color.FromArgb(255, 0x12, 0xA2, 0x77), 1.5f) { LineJoin = LineJoin.Round };
        PointF[] pts = points.Select(p => new PointF(
            rect.X + (float)p.ElapsedFraction * rect.Width,
            rect.Bottom - (float)Math.Clamp(p.Pct / 100.0, 0.0, 1.0) * rect.Height)).ToArray();
        g.DrawLines(pen, pts);
    }

    void DrawHeatCell(Graphics g, HeatCellRect cell)
    {
        Color color = cell.Day.Level switch
        {
            HeatmapLevel.HitCeiling => HeatCeiling,
            HeatmapLevel.Normal => HeatNormal,
            HeatmapLevel.Incomplete => HeatIncomplete,
            _ => HeatNoData,
        };
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, cell.Rect);
    }

    void Content_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_layout is not { } layout) return;
        PointF logical = new(e.X - _content.AutoScrollPosition.X, e.Y - _content.AutoScrollPosition.Y);
        foreach (HeatCellRect cell in layout.HeatCells)
        {
            if (cell.Rect.Contains(logical))
            {
                if (_lastHeatmapTooltip != cell.Day.Tooltip)
                {
                    _lastHeatmapTooltip = cell.Day.Tooltip;
                    _heatmapTip.Show(cell.Day.Tooltip, _content, e.X + 12, e.Y + 12, 4000);
                }
                return;
            }
        }
        HideHeatmapTip();
    }

    void HideHeatmapTip()
    {
        if (_lastHeatmapTooltip is null) return;
        _lastHeatmapTooltip = null;
        _heatmapTip.Hide(_content);
    }

    // ---- text fitting (same algorithm as Ui/PanelForm.cs's DrawFitText -- shrink-then-wrap, never clipped) ----

    float DrawFitText(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, bool draw = true)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        if (maxWidth <= 0f) maxWidth = 1f;

        SizeF oneLine = g.MeasureString(text, font, int.MaxValue);
        if (oneLine.Width <= maxWidth)
        {
            if (draw) DrawString(g, text, font, color, x, y, maxWidth, oneLine.Height);
            return oneLine.Height;
        }

        using var shrunk = new Font(font.FontFamily, Math.Max(7f, font.Size - 1f), font.Style, GraphicsUnit.Pixel);
        SizeF shrunkOneLine = g.MeasureString(text, shrunk, int.MaxValue);
        if (shrunkOneLine.Width <= maxWidth)
        {
            if (draw) DrawString(g, text, shrunk, color, x, y, maxWidth, shrunkOneLine.Height);
            return shrunkOneLine.Height;
        }

        SizeF wrapped = g.MeasureString(text, shrunk, (int)Math.Ceiling(maxWidth));
        if (draw) DrawString(g, text, shrunk, color, x, y, maxWidth, wrapped.Height);
        return wrapped.Height;
    }

    static void DrawString(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, float height)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(x, y, maxWidth, height + 2));
    }

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

    static string ResolveFamily(string preferred, string fallback)
    {
        try { using var f = new FontFamily(preferred); return f.Name; }
        catch (ArgumentException) { return fallback; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _measureG.Dispose();
            _measureBmp.Dispose();
            _heatmapTip.Dispose();
            foreach (Font f in new[] { _fontTitle, _fontSectionTitle, _fontTileTitle, _fontTileValue, _fontTileValueLearning, _fontTileN, _fontBody, _fontSmall })
                f.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The scrollable content area; painting lives on the parent form so it shares fonts/measuring state, this class just forwards OnPaint.</summary>
    sealed class ContentPanel : Panel
    {
        readonly StatisticsForm _owner;
        public ContentPanel(StatisticsForm owner) { _owner = owner; DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            try { _owner.PaintContent(e.Graphics); }
            catch (Exception ex) { SafeLog.Warn($"StatisticsForm paint threw: {ex.Message}"); }
        }
    }
}

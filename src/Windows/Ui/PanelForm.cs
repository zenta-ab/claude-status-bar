using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// The tray flyout: a borderless, custom-painted, never-activated popup showing
/// the complete quota panel, panel-v2 "answer first" layout (docs/panel-v2.md).
/// Window-style approach (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST +
/// ShowWithoutActivation) is ported from the measured probe
/// (scratchpad/tbtest/Panel.cs), which confirmed GetForegroundWindow() is
/// unchanged before, after and 1.8s after Show().
///
/// Laid out in logical (96-DPI) pixels, 340 wide, then rendered through a single
/// Graphics.ScaleTransform(scale) so every coordinate in OnPaint stays in that
/// same logical space regardless of the monitor's actual DPI -- the physical
/// window Size is the only thing computed in device pixels (PanelAnchor.Resolve).
///
/// Wording comes from PanelText.Compose (pure text, no drawing); this class only
/// paints it. Height fits content: BuildLayout() is the single source of truth for
/// every section's Y-offset AND for text wrapping/shrinking (DrawFitText doubles as
/// both the measuring pass and the painting pass, so they can never drift apart).
/// The panel's content height varies with wrapped line counts (a long status-box
/// verdict, a secondary line, the Stale note), so it is re-measured and the window
/// re-anchored (bottom edge 12px above the taskbar) on every UpdateView that
/// changes it -- see PanelAnchor.Resolve, which this re-derives from.
///
/// A fresh instance of this form is reused for the app's whole lifetime; it is
/// recreated from PanelAnchor.Resolve on every ShowPanel()/height change rather
/// than kept positioned/scaled, since it is an ephemeral, non-draggable popup --
/// there is no live-DPI-change or monitor-move path to handle.
/// </summary>
public sealed class PanelForm : Form
{
    const int LogicalWidth = 340;
    const float SidePadding = 14f;
    const float RingPx = 28f;
    const float BarHeight = 8f;
    const float RefreshGlyphSize = 13f;
    const float RefreshHitSize = 22f; // bigger than the glyph itself -- easier to click, Fitts's-law style

    // Same literal as PanelText's AwaitingResetText -- QuotaModel/DemoQuotaSource's
    // MeasuringReason is a plain string, not an enum, and this is the one place
    // outside PanelText that needs to recognize it: the Kvot bar must draw empty,
    // not the stale/old window's usage, while a window awaits its next observation
    // (docs/panel-v2.md, item 2).
    const string AwaitingResetText = "Nytt fönster väntas";

    /// <summary>
    /// Set from Program.cs when --capture-states is present: gates the "OVERFLOW
    /// &lt;text&gt;" diagnostic (docs/panel-v2.md's width guard) so it only fires
    /// during the automated capture run this task's verification step reads,
    /// not on every normal paint.
    /// </summary>
    public static bool DiagnosticsEnabled { get; set; }

    static readonly Color PanelBackground = Color.FromArgb(255, 0x20, 0x20, 0x20);
    static readonly Color BorderColor = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);
    static readonly Color RuleColor = Color.FromArgb(255, 0x33, 0x33, 0x33);
    static readonly Color TextPrimary = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    static readonly Color TextSecondary = Color.FromArgb(255, 0x9A, 0x9A, 0x9A);
    static readonly Color TextTertiary = Color.FromArgb(255, 0x70, 0x70, 0x70);
    static readonly Color DemoBadgeBg = Color.FromArgb(255, 0xE8, 0xA0, 0x20);
    static readonly Color TidTrack = Color.FromArgb(28, 255, 255, 255);
    static readonly Color TidFill = Color.FromArgb(120, 255, 255, 255);
    static readonly Color BarTrack = Color.FromArgb(22, 255, 255, 255);

    static readonly string DisplayFamily = ResolveFamily("Segoe UI Variable Display", "Segoe UI");
    static readonly string TextFamily = ResolveFamily("Segoe UI Variable Text", "Segoe UI");

    NotifyIcon? _trayIcon;
    readonly DismissWatcher _dismissWatcher;
    readonly System.Windows.Forms.Timer _tickTimer;
    readonly Bitmap _measureBmp = new(1, 1);
    readonly Graphics _measureG;

    readonly Font _fontTitle;
    readonly Font _fontFreshness;
    readonly Font _fontStatusLine1;
    readonly Font _fontStatusLine2;
    readonly Font _fontStatusLine3;
    readonly Font _fontSectionTitle;
    readonly Font _fontResetHeader;
    readonly Font _fontBarCaption;
    readonly Font _fontBarLabel;
    readonly Font _fontFooter;
    readonly Font _fontDemoBadge;

    float _scale = 1f;
    QuotaView _view = QuotaView.Initial;
    bool _demo;
    float _lastHeight = -1f;
    string? _accountLabel;
    IReadOnlyList<OtherAccountRow> _otherAccounts = Array.Empty<OtherAccountRow>();
    bool _refreshInFlight;

    /// <summary>
    /// docs/multi-account.md "Panel": fired when the user clicks one of the "other accounts"
    /// rows, with that row's AccountIndex -- the caller (StatusBarApplicationContext) owns what
    /// that index means and switches the panel to it.
    /// </summary>
    public event Action<int>? OtherAccountClicked;

    /// <summary>
    /// The task's reload button, header row: fired when the user clicks the refresh control
    /// while it is not already busy. The caller (StatusBarApplicationContext) owns which
    /// account is currently shown and triggers that account's AccountRuntime.
    /// RequestImmediateRefresh -- this form only knows "the user asked to refresh right now",
    /// never which account that means.
    /// </summary>
    public event Action? RefreshRequested;

    public PanelForm(NotifyIcon? trayIcon = null)
    {
        _trayIcon = trayIcon;
        _dismissWatcher = new DismissWatcher(this);
        _measureG = Graphics.FromImage(_measureBmp);

        Text = "ClaudeStatusBarPanel"; // FormBorderStyle.None hides the title bar, but this still names the window for FindWindow-based tooling/tests
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = PanelBackground;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _fontTitle = new Font(DisplayFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontFreshness = new Font(TextFamily, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontStatusLine1 = new Font(DisplayFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontStatusLine2 = new Font(TextFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontStatusLine3 = new Font(TextFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontSectionTitle = new Font(TextFamily, 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontResetHeader = new Font(TextFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontBarCaption = new Font(TextFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontBarLabel = new Font(TextFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontFooter = new Font(TextFamily, 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontDemoBadge = new Font(TextFamily, 9f, FontStyle.Bold, GraphicsUnit.Pixel);

        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) =>
        {
            // Targeted try/finally (Codex review Medium #15): a WinForms Timer.Tick exception
            // escapes onto the message loop, so this must never propagate.
            try { Invalidate(); }
            catch (Exception ex) { SafeLog.Warn($"PanelForm tick Invalidate threw: {ex.Message}"); }
        };

        _dismissWatcher.DismissRequested += HidePanel;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;

    /// <summary>
    /// ShowWithoutActivation + WS_EX_NOACTIVATE cover most activation routes, but not every
    /// one -- Windows' active-window tracking can still send WM_MOUSEACTIVATE on hover
    /// (Codex review Medium #14). Answering MA_NOACTIVATE here is the explicit,
    /// belt-and-braces rejection so the "never activates" guarantee has no gap.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Called from StatusBarApplicationContext through the same SynchronizationContext.Post path
    /// the tray icon uses, at 1Hz.
    /// </summary>
    /// <param name="accountLabel">
    /// docs/multi-account.md "Panel": the shown account's label for the header, or null to keep
    /// the panel pixel-identical to the single-account layout -- StatusBarApplicationContext only
    /// passes a non-null label when more than one account is configured.
    /// </param>
    /// <param name="otherAccounts">The compact "other accounts" rows at the bottom, empty when there is only one account.</param>
    /// <param name="refreshInFlight">
    /// The task's reload button: true while a poll for the SHOWN account is in flight (whether
    /// started by the button or by the account's own timer) -- the caller reads this off
    /// AccountRuntime.IsPolling for whichever account it just passed in `view`. The control
    /// renders dimmed and ignores clicks while this is true, so it can never start a second
    /// concurrent poll for the same account.
    /// </param>
    public void UpdateView(QuotaView view, bool demo, string? accountLabel = null, IReadOnlyList<OtherAccountRow>? otherAccounts = null, bool refreshInFlight = false)
    {
        try
        {
            _view = view;
            _demo = demo;
            _accountLabel = accountLabel;
            _otherAccounts = otherAccounts ?? Array.Empty<OtherAccountRow>();
            _refreshInFlight = refreshInFlight;

            if (Visible)
            {
                float height = BuildLayout(DateTimeOffset.UtcNow).TotalHeight;
                if (Math.Abs(height - _lastHeight) > 0.5f) Reanchor();
                else Invalidate();
            }
        }
        catch (Exception ex)
        {
            // UI exception boundary (Codex review Medium #15): a layout/measurement failure
            // must not crash the 1Hz eval tick that drives this. The panel keeps showing its
            // last successfully built content until the next tick's attempt succeeds.
            SafeLog.Warn($"PanelForm.UpdateView threw: {ex.Message}");
        }
    }

    public void Toggle()
    {
        if (Visible) HidePanel(); else ShowPanel();
    }

    /// <summary>
    /// docs/multi-account.md "Display": with per-account icons, the panel should anchor near
    /// whichever icon it is currently showing the account for, not a single icon fixed at
    /// construction. Called from StatusBarApplicationContext whenever the shown account (or its
    /// icon) changes; safe to call with null (e.g. every account momentarily has no icon) --
    /// PanelAnchor then falls back to the work area's bottom-right corner.
    /// </summary>
    public void SetAnchorIcon(NotifyIcon? trayIcon) => _trayIcon = trayIcon;

    /// <summary>
    /// docs/multi-account.md "Panel": clicking an "other accounts" row switches the panel to
    /// that account. WS_EX_NOACTIVATE/ShowWithoutActivation only suppress activation -- ordinary
    /// mouse messages (WM_LBUTTONDOWN) are still delivered to whichever window is under the
    /// cursor, so this fires normally without the panel ever taking focus.
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        try
        {
            if (e.Button != MouseButtons.Left || _scale <= 0f) return;
            var logicalPoint = new PointF(e.X / _scale, e.Y / _scale);

            PanelLayout layout = BuildLayout(DateTimeOffset.UtcNow);

            // Checked first regardless of _otherAccounts: the reload button lives in the header,
            // well above the other-accounts rows, so there is no coordinate overlap to arbitrate.
            // Ignored while already busy -- this is the actual guard against a click starting a
            // second concurrent poll for the shown account (StatusBarApplicationContext.IsPolling
            // is the other half, for the timer-driven case).
            if (!_refreshInFlight && layout.RefreshHitRect.Contains(logicalPoint))
            {
                RefreshRequested?.Invoke();
                return;
            }

            if (_otherAccounts.Count == 0) return;
            for (int i = 0; i < layout.OtherAccountRowRects.Count && i < _otherAccounts.Count; i++)
            {
                if (layout.OtherAccountRowRects[i].Contains(logicalPoint))
                {
                    OtherAccountClicked?.Invoke(_otherAccounts[i].AccountIndex);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"PanelForm.OnMouseDown threw: {ex.Message}");
        }
    }

    public void ShowPanel()
    {
        if (Visible) return;

        PanelLayout layout = BuildLayout(DateTimeOffset.UtcNow);
        _lastHeight = layout.TotalHeight;
        var (location, size, scale) = PanelAnchor.Resolve(_trayIcon, new Size(LogicalWidth, (int)Math.Ceiling(layout.TotalHeight)));
        _scale = scale;
        Size = size;
        Location = location;

        Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE: never takes focus or foreground
        _tickTimer.Start();

        // The tray icon's own rectangle is excluded from click-away dismissal (Codex review
        // Medium #13): otherwise a click ON the tray icon to close the panel lands as
        // "outside the panel" here, hides it, and then the tray's own MouseClick handler
        // (StatusBarApplicationContext) sees a hidden panel and reopens it -- one click that
        // should close the panel instead leaves it open.
        _dismissWatcher.Start(
            screenPoint => Bounds.Contains(screenPoint),
            screenPoint => PanelAnchor.TryGetIconRect(_trayIcon, out Rectangle iconRect) && iconRect.Contains(screenPoint));
    }

    public void HidePanel()
    {
        if (!Visible) return;
        _dismissWatcher.Stop();
        _tickTimer.Stop();
        Hide();
    }

    /// <summary>Bottom edge stays 12px above the taskbar even as content height changes (wrapped status-box lines, secondary line, Stale note appearing/disappearing).</summary>
    void Reanchor()
    {
        PanelLayout layout = BuildLayout(DateTimeOffset.UtcNow);
        _lastHeight = layout.TotalHeight;
        var (location, size, scale) = PanelAnchor.Resolve(_trayIcon, new Size(LogicalWidth, (int)Math.Ceiling(layout.TotalHeight)));
        _scale = scale;
        Bounds = new Rectangle(location, size);
        Invalidate();
    }

    // ---- layout: single source of truth for every section's Y-offset (and, via DrawFitText, every wrap decision) ----

    readonly record struct PanelLayout(
        PanelTextResult Text, float LabelY, float FreshnessY, float StatusBoxY, float StatusBoxHeight,
        float SessionY, float SessionHeight, float WeeklyY, float WeeklyHeight,
        float RuleY, float FooterY, float OtherAccountsRuleY, float OtherAccountsY,
        IReadOnlyList<RectangleF> OtherAccountRowRects, RectangleF RefreshHitRect, float TotalHeight);

    float ContentWidth => LogicalWidth - 2 * SidePadding;

    PanelLayout BuildLayout(DateTimeOffset now)
    {
        PanelTextResult text = PanelText.Compose(_view, now, TimeZoneInfo.Local);

        float y = 12f;
        y += 20f; // title

        float labelY = -1f;
        if (_accountLabel is { } label)
        {
            labelY = y;
            y += DrawFitText(_measureG, label, _fontSectionTitle, TextPrimary, 0, 0, ContentWidth, draw: false) + 4f;
        }

        float freshnessY = y;
        const float freshnessRowHeight = 22f;
        var refreshHitRect = new RectangleF(
            LogicalWidth - SidePadding - RefreshHitSize,
            freshnessY + (freshnessRowHeight - RefreshHitSize) / 2f,
            RefreshHitSize, RefreshHitSize);
        y += freshnessRowHeight;

        float statusBoxY = y;
        float statusBoxHeight = MeasureStatusBox(text.StatusBox);
        y += statusBoxHeight + 14f;

        float sessionY = y;
        float sessionHeight = MeasureSection(text.Session, _view.Session);
        y += sessionHeight + 16f;

        float weeklyY = y;
        float weeklyHeight = MeasureSection(text.Weekly, _view.Weekly);
        y += weeklyHeight + 14f;

        float ruleY = y;
        y += 12f;
        float footerY = y;
        y += 20f;

        float otherAccountsRuleY = -1f;
        float otherAccountsY = -1f;
        var rowRects = new List<RectangleF>();
        if (_otherAccounts.Count > 0)
        {
            y += 10f;
            otherAccountsRuleY = y;
            y += 10f;
            otherAccountsY = y;
            y += DrawFitText(_measureG, "ANDRA KONTON", _fontBarCaption, TextTertiary, 0, 0, ContentWidth, draw: false) + 4f;

            foreach (OtherAccountRow row in _otherAccounts)
            {
                string rowText = $"{row.Label}  {row.Line}";
                float rowTop = y;
                float rowHeight = DrawFitText(_measureG, rowText, _fontBarLabel, TextPrimary, 0, 0, ContentWidth, draw: false);
                rowRects.Add(new RectangleF(SidePadding, rowTop, ContentWidth, rowHeight));
                y += rowHeight + 6f;
            }
        }

        return new PanelLayout(text, labelY, freshnessY, statusBoxY, statusBoxHeight, sessionY, sessionHeight,
            weeklyY, weeklyHeight, ruleY, footerY, otherAccountsRuleY, otherAccountsY, rowRects, refreshHitRect, y);
    }

    float MeasureStatusBox(StatusBoxText box)
    {
        float innerWidth = ContentWidth - 14f - 12f;
        float h = 10f;
        h += DrawFitText(_measureG, box.Line1, _fontStatusLine1, TextPrimary, 0, 0, innerWidth, draw: false);
        h += 4f;
        h += DrawFitText(_measureG, box.Line2, _fontStatusLine2, TextSecondary, 0, 0, innerWidth, draw: false);
        if (box.SecondaryLine is { } sec) { h += 4f; h += DrawFitText(_measureG, sec, _fontStatusLine3, TextSecondary, 0, 0, innerWidth, draw: false); }
        if (box.Line3 is { } l3) { h += 4f; h += DrawFitText(_measureG, l3, _fontStatusLine3, Palette.Tight, 0, 0, innerWidth, draw: false); }
        h += 10f;
        return h;
    }

    float MeasureSection(WindowSectionText section, WindowView w)
    {
        float y = RingPx; // title row: ring height or title text, whichever taller (ring dominates at 28px)
        y += 4f;
        y += DrawFitText(_measureG, section.ResetHeader, _fontResetHeader, TextSecondary, 0, 0, ContentWidth, draw: false);
        y += 6f;
        y += _fontBarCaption.Height + 2f; // "Tid" caption + bar row
        y += BarHeight + 4f;
        y += DrawFitText(_measureG, section.TidLabel, _fontBarLabel, TextSecondary, 0, 0, ContentWidth, draw: false);
        y += 8f;
        y += _fontBarCaption.Height + 2f; // "Kvot" caption + bar row
        y += BarHeight + 4f;
        y += DrawFitText(_measureG, section.KvotLabel, _fontBarLabel, TextSecondary, 0, 0, ContentWidth, draw: false);
        return y;
    }

    // ---- paint ----

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            Graphics g = e.Graphics;
            g.Clear(PanelBackground);
            using (var border = new Pen(BorderColor))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(_scale, _scale); // everything from here on is in logical (96-DPI) px

            DateTimeOffset now = DateTimeOffset.UtcNow;
            PanelLayout layout = BuildLayout(now);

            DrawHeader(g, layout);
            DrawStatusBox(g, layout);
            DrawSection(g, layout.SessionY, layout.Text.Session, _view.Session, now);
            DrawSection(g, layout.WeeklyY, layout.Text.Weekly, _view.Weekly, now);

            using (var rulePen = new Pen(RuleColor))
                g.DrawLine(rulePen, SidePadding, layout.RuleY, LogicalWidth - SidePadding, layout.RuleY);

            using (var footerBrush = new SolidBrush(TextTertiary))
                g.DrawString(BuildFooter(_view), _fontFooter, footerBrush, SidePadding, layout.FooterY);

            DrawOtherAccounts(g, layout);
        }
        catch (Exception ex)
        {
            // UI exception boundary (Codex review Medium #15): a paint failure hides the
            // panel rather than leaving a broken/partial render on screen or crashing the
            // message loop. Deferred via BeginInvoke -- Hide() must not run reentrantly from
            // inside OnPaint itself.
            SafeLog.Warn($"PanelForm.OnPaint threw: {ex.Message}");
            try { BeginInvoke(new Action(HidePanel)); } catch { /* handle may already be gone */ }
        }
    }

    void DrawHeader(Graphics g, PanelLayout layout)
    {
        using (var titleBrush = new SolidBrush(TextPrimary))
            g.DrawString("Claude Code", _fontTitle, titleBrush, SidePadding, 12f);

        if (_demo)
        {
            const float badgeW = 46f, badgeH = 16f;
            var badgeRect = new RectangleF(LogicalWidth - SidePadding - badgeW, 13f, badgeW, badgeH);
            using (var path = RoundedRect(badgeRect, 4f))
            using (var bg = new SolidBrush(DemoBadgeBg))
                g.FillPath(bg, path);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Color.Black);
            g.DrawString("DEMO", _fontDemoBadge, textBrush, badgeRect, fmt);
        }

        if (_accountLabel is { } label)
            DrawFitText(g, label, _fontSectionTitle, TextPrimary, SidePadding, layout.LabelY, ContentWidth);

        var (text, color) = BuildFreshnessLine(_view);
        using var freshBrush = new SolidBrush(color);
        g.DrawString(text, _fontFreshness, freshBrush, SidePadding, layout.FreshnessY);

        DrawRefreshGlyph(g, layout.RefreshHitRect);
    }

    /// <summary>
    /// The task's reload button: a plain painted hit-rect (never a real WinForms Button --
    /// that would risk the panel's own no-activation guarantee), next to the freshness line.
    /// Drawn as a circular-arrow "refresh" glyph, dimmed and click-inert while _refreshInFlight
    /// (see OnMouseDown and UpdateView's refreshInFlight parameter).
    /// </summary>
    void DrawRefreshGlyph(Graphics g, RectangleF hitRect)
    {
        var glyphRect = new RectangleF(
            hitRect.X + (hitRect.Width - RefreshGlyphSize) / 2f,
            hitRect.Y + (hitRect.Height - RefreshGlyphSize) / 2f,
            RefreshGlyphSize, RefreshGlyphSize);

        Color color = _refreshInFlight ? TextTertiary : TextSecondary;
        float thickness = Math.Max(1.2f, RefreshGlyphSize * 0.16f);

        using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            const float startAngle = -210f;
            const float sweepAngle = 240f; // leaves a gap at the end for the arrowhead
            g.DrawArc(pen, glyphRect, startAngle, sweepAngle);

            // Arrowhead tangent to the arc's end, pointing in its direction of travel
            // (clockwise): a small triangle built pointing along +X, then rotated/translated
            // into place -- simpler and less error-prone than composing the wing vectors by hand.
            const float endAngle = startAngle + sweepAngle;
            float r = glyphRect.Width / 2f;
            float cx = glyphRect.X + r, cy = glyphRect.Y + r;
            double endRad = endAngle * Math.PI / 180.0;
            float tipX = cx + r * (float)Math.Cos(endRad);
            float tipY = cy + r * (float)Math.Sin(endRad);

            float headSize = RefreshGlyphSize * 0.42f;
            using var head = new GraphicsPath();
            head.AddPolygon(new[]
            {
                new PointF(0, -headSize * 0.55f),
                new PointF(headSize, 0),
                new PointF(0, headSize * 0.55f),
            });
            using (var m = new Matrix())
            {
                // Default MatrixOrder.Prepend: the LAST-called operation is applied to the local
                // shape first, so this rotates the triangle (defined around its own origin) to
                // the tangent direction, THEN moves it to the arc's end point -- not the reverse.
                m.Translate(tipX, tipY);
                m.Rotate(endAngle + 90f); // tangent direction at this point on the circle (see DrawArc's angle convention)
                head.Transform(m);
            }

            using var headBrush = new SolidBrush(color);
            g.FillPath(headBrush, head);
        }
    }

    /// <summary>
    /// docs/multi-account.md "Panel": the compact "other accounts" list at the bottom, only when
    /// there is more than one account (StatusBarApplicationContext passes an empty list
    /// otherwise). Each row's screen rectangle was already computed in BuildLayout -- OnMouseDown
    /// rebuilds the same layout to hit-test against it, so painting and hit-testing can never
    /// disagree about where a row is.
    /// </summary>
    void DrawOtherAccounts(Graphics g, PanelLayout layout)
    {
        if (_otherAccounts.Count == 0) return;

        using (var rulePen = new Pen(RuleColor))
            g.DrawLine(rulePen, SidePadding, layout.OtherAccountsRuleY, LogicalWidth - SidePadding, layout.OtherAccountsRuleY);

        DrawFitText(g, "ANDRA KONTON", _fontBarCaption, TextTertiary, SidePadding, layout.OtherAccountsY, ContentWidth);

        for (int i = 0; i < _otherAccounts.Count; i++)
        {
            OtherAccountRow row = _otherAccounts[i];
            string rowText = $"{row.Label}  {row.Line}";
            Color color = RoleColor(row.Role);
            DrawFitText(g, rowText, _fontBarLabel, color, SidePadding, layout.OtherAccountRowRects[i].Y, ContentWidth);
        }
    }

    /// <summary>Status box (docs/panel-v2.md, item 2) -- ALWAYS shown. Line 1 is the verdict in the role colour; line 2 always states when.</summary>
    void DrawStatusBox(Graphics g, PanelLayout layout)
    {
        StatusBoxText box = layout.Text.StatusBox;
        var rect = new RectangleF(SidePadding, layout.StatusBoxY, ContentWidth, layout.StatusBoxHeight);
        Color color = RoleColor(box.Role);

        using (var path = RoundedRect(rect, 6f))
        using (var bg = new SolidBrush(Color.FromArgb(24, color)))
            g.FillPath(bg, path);
        using (var accent = new SolidBrush(color))
            g.FillRectangle(accent, rect.X, rect.Y, 3f, rect.Height);

        float textX = rect.X + 14f;
        float innerWidth = rect.Width - 14f - 12f;
        float y = rect.Y + 10f;

        y += DrawFitText(g, box.Line1, _fontStatusLine1, color, textX, y, innerWidth) + 4f;
        y += DrawFitText(g, box.Line2, _fontStatusLine2, TextSecondary, textX, y, innerWidth) + 4f;
        if (box.SecondaryLine is { } sec) y += DrawFitText(g, sec, _fontStatusLine3, TextSecondary, textX, y, innerWidth) + 4f;
        if (box.Line3 is { } l3) DrawFitText(g, l3, _fontStatusLine3, Palette.Tight, textX, y, innerWidth);
    }

    /// <summary>One window section (docs/panel-v2.md, item 3): small ring, title, reset header, Tid bar, Kvot bar.</summary>
    void DrawSection(Graphics g, float sectionY, WindowSectionText section, WindowView w, DateTimeOffset now)
    {
        float ringX = SidePadding;
        Color ringColor = w.State == QuotaState.Spent ? Palette.Dead : Palette.ColorForState(w.State);
        double ringFrac = w.State == QuotaState.Measuring ? 0.0 : Math.Clamp((w.UsedPct ?? 0.0) / 100.0, 0.0, 1.0);
        using (var ring = GaugeRenderer.RenderRing((int)RingPx, ringFrac, ringColor, exhausted: w.State == QuotaState.Spent))
            g.DrawImage(ring, ringX, sectionY, RingPx, RingPx);

        using (var titleBrush = new SolidBrush(TextSecondary))
        using (var titleFmt = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(section.Title, _fontSectionTitle, titleBrush, new RectangleF(ringX + RingPx + 8f, sectionY, ContentWidth - RingPx - 8f, RingPx), titleFmt);

        float y = sectionY + RingPx + 4f;
        y += DrawFitText(g, section.ResetHeader, _fontResetHeader, TextTertiary, SidePadding, y, ContentWidth, StringAlignment.Far) + 6f;

        using (var capBrush = new SolidBrush(TextTertiary))
            g.DrawString("Tid", _fontBarCaption, capBrush, SidePadding, y);
        DrawTidBar(g, new RectangleF(SidePadding, y + _fontBarCaption.Height + 2f, ContentWidth, BarHeight), w, now);
        y += _fontBarCaption.Height + 2f + BarHeight + 4f;
        y += DrawFitText(g, section.TidLabel, _fontBarLabel, TextSecondary, SidePadding, y, ContentWidth) + 8f;

        using (var capBrush = new SolidBrush(TextTertiary))
            g.DrawString("Kvot", _fontBarCaption, capBrush, SidePadding, y);
        DrawKvotBar(g, new RectangleF(SidePadding, y + _fontBarCaption.Height + 2f, ContentWidth, BarHeight), w);
        y += _fontBarCaption.Height + 2f + BarHeight + 4f;
        Color labelColor = w.State == QuotaState.DryEarly ? Palette.Crit : w.State == QuotaState.Spent ? Palette.Dead : TextSecondary;
        DrawFitText(g, section.KvotLabel, _fontBarLabel, labelColor, SidePadding, y, ContentWidth);
    }

    /// <summary>Elapsed-time fraction, neutral colour, never a forecast segment (docs/panel-v2.md, item 3).</summary>
    void DrawTidBar(Graphics g, RectangleF rect, WindowView w, DateTimeOffset now)
    {
        FillBarTrack(g, rect);
        if (w.ResetsAt is not { } resetsAt) return;
        double frac = Math.Clamp((w.WindowMinutes - (resetsAt - now).TotalMinutes) / w.WindowMinutes, 0.0, 1.0);
        using var fill = new SolidBrush(TidFill);
        g.FillRectangle(fill, rect.X, rect.Y, (float)frac * rect.Width, rect.Height);
    }

    /// <summary>
    /// Used part solid in the state colour, then a forecast segment (same colour,
    /// ~40% alpha, thin outline) to min(projected, 100). Projected past 100% draws
    /// the segment to the end in the DryEarly colour with a marker (docs/panel-v2.md, item 3).
    /// </summary>
    void DrawKvotBar(Graphics g, RectangleF rect, WindowView w)
    {
        FillBarTrack(g, rect);

        if (w.State == QuotaState.Measuring)
        {
            // Awaiting reset: the % on this WindowView is the OLD window's last known
            // usage, not the new window's -- drawing any fill here would show it as if
            // still current (item 2). Track only, same as an empty bar.
            if (w.MeasuringReason == AwaitingResetText) return;

            double measFrac = Math.Clamp((w.UsedPct ?? 0.0) / 100.0, 0.0, 1.0);
            using var fill = new SolidBrush(Palette.Measuring);
            g.FillRectangle(fill, rect.X, rect.Y, (float)measFrac * rect.Width, rect.Height);
            return;
        }

        if (w.State == QuotaState.Spent)
        {
            using var fill = new SolidBrush(Palette.Dead);
            g.FillRectangle(fill, rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }

        Color stateColor = Palette.ColorForState(w.State);
        double usedFrac = Math.Clamp((w.UsedPct ?? 0.0) / 100.0, 0.0, 1.0);
        using (var fill = new SolidBrush(stateColor))
            g.FillRectangle(fill, rect.X, rect.Y, (float)usedFrac * rect.Width, rect.Height);

        if (w.ProjectedPctAtReset is not { } proj) return;
        double projFrac = proj / 100.0;
        if (projFrac <= usedFrac) return;

        bool overflow = projFrac > 1.0;
        double endFrac = Math.Min(1.0, projFrac);
        Color forecastColor = overflow ? Palette.Crit : stateColor;

        float startX = rect.X + (float)usedFrac * rect.Width;
        float endX = rect.X + (float)endFrac * rect.Width;
        using (var forecastFill = new SolidBrush(Color.FromArgb(102, forecastColor))) // ~40% alpha
            g.FillRectangle(forecastFill, startX, rect.Y, endX - startX, rect.Height);
        using (var outline = new Pen(forecastColor, 1f))
            g.DrawRectangle(outline, startX, rect.Y, endX - startX, rect.Height);

        if (overflow)
        {
            using var markerPen = new Pen(Palette.Crit, 2f);
            g.DrawLine(markerPen, endX, rect.Y - 2f, endX, rect.Bottom + 2f);
        }
    }

    void FillBarTrack(Graphics g, RectangleF rect)
    {
        using var track = new SolidBrush(BarTrack);
        g.FillRectangle(track, rect);
    }

    // ---- text fitting: the width guard (docs/panel-v2.md's "Never clip"). Shared by the
    // measuring pass (BuildLayout, draw:false) and the painting pass, so they can never disagree. ----

    /// <summary>
    /// Tries the text as-is; if it overflows maxWidth, shrinks the font one step; if it
    /// still overflows, wraps at the shrunk font (word-wrap, never clipped). Logs
    /// "OVERFLOW &lt;text&gt;" (when DiagnosticsEnabled) only for the pathological case of
    /// a single word still wider than maxWidth even after both strategies.
    /// </summary>
    float DrawFitText(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, bool draw = true) =>
        DrawFitText(g, text, font, color, x, y, maxWidth, StringAlignment.Near, draw);

    float DrawFitText(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, StringAlignment align, bool draw = true)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        SizeF oneLine = g.MeasureString(text, font, int.MaxValue);
        if (oneLine.Width <= maxWidth)
        {
            if (draw) DrawSingleOrWrapped(g, text, font, color, x, y, maxWidth, oneLine.Height, align);
            return oneLine.Height;
        }

        using var shrunk = new Font(font.FontFamily, Math.Max(7f, font.Size - 1f), font.Style, GraphicsUnit.Pixel);
        SizeF shrunkOneLine = g.MeasureString(text, shrunk, int.MaxValue);
        if (shrunkOneLine.Width <= maxWidth)
        {
            if (draw) DrawSingleOrWrapped(g, text, shrunk, color, x, y, maxWidth, shrunkOneLine.Height, align);
            return shrunkOneLine.Height;
        }

        SizeF wrapped = g.MeasureString(text, shrunk, (int)Math.Ceiling(maxWidth));
        CheckOverflow(g, text, shrunk, maxWidth);
        if (draw) DrawSingleOrWrapped(g, text, shrunk, color, x, y, maxWidth, wrapped.Height, align);
        return wrapped.Height;
    }

    static void DrawSingleOrWrapped(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, float height, StringAlignment align)
    {
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = align };
        g.DrawString(text, font, brush, new RectangleF(x, y, maxWidth, height + 2), fmt);
    }

    static void CheckOverflow(Graphics g, string text, Font font, float maxWidth)
    {
        if (!DiagnosticsEnabled) return;
        foreach (string word in text.Split(' '))
        {
            if (g.MeasureString(word, font, int.MaxValue).Width > maxWidth)
            {
                Console.WriteLine($"OVERFLOW {text}");
                return;
            }
        }
    }

    // ---- text builders that stay in PanelForm: not part of what PanelText.Compose returns ----

    static (string Text, Color Color) BuildFreshnessLine(QuotaView view)
    {
        if (view.Freshness == Freshness.Unknown) return ("Kan inte läsa kvoten", Palette.Crit);
        if (view.LastChangedAt is { } changed)
        {
            TimeSpan age = DateTimeOffset.Now - changed;
            string text = $"Uppdaterad för {TimeText.Duration(age)} sedan";
            return (text, view.Freshness == Freshness.Stale ? Palette.Tight : TextSecondary);
        }
        return ("Hämtar…", TextSecondary);
    }

    static string BuildFooter(QuotaView view) =>
        view.LastPollAt is { } lastPoll
            ? $"Senast avläst kl {TimeText.ClockWithSeconds(lastPoll, TimeZoneInfo.Local)} · uppdateras var {TimeText.Duration(view.PollInterval)}"
            : "Väntar på första avläsningen…";

    static Color RoleColor(PanelColorRole role) => role switch
    {
        PanelColorRole.Safe => Palette.Ok,
        PanelColorRole.Tight => Palette.Tight,
        PanelColorRole.Crit => Palette.Crit,
        PanelColorRole.Dead => Palette.Dead,
        PanelColorRole.Unknown => Palette.Crit,
        _ => Palette.Measuring,
    };

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

    /// <summary>Segoe UI Variable is Windows 11+ only; falls back to plain Segoe UI when the family isn't installed.</summary>
    static string ResolveFamily(string preferred, string fallback)
    {
        try
        {
            using var f = new FontFamily(preferred);
            return f.Name;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismissWatcher.Dispose();
            _tickTimer.Dispose();
            _measureG.Dispose();
            _measureBmp.Dispose();
            _fontTitle.Dispose();
            _fontFreshness.Dispose();
            _fontStatusLine1.Dispose();
            _fontStatusLine2.Dispose();
            _fontStatusLine3.Dispose();
            _fontSectionTitle.Dispose();
            _fontResetHeader.Dispose();
            _fontBarCaption.Dispose();
            _fontBarLabel.Dispose();
            _fontFooter.Dispose();
            _fontDemoBadge.Dispose();
        }
        base.Dispose(disposing);
    }

    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_TOPMOST = 0x00000008;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

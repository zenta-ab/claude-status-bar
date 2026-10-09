using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// Everything one rendering of the full panel shows: the account's quota view, its header texts, the
/// "other accounts" rows, the reload button's busy flag, the advice line. The form draws ONE model in
/// single mode and N of them side by side in full mode with exactly the same layout and drawing code.
/// Slot is the account the panel's own controls (refresh, "Logga in igen") act on.
/// </summary>
public sealed record PanelModel(
    string? Slot, QuotaView View, string? Label, string? Subtitle,
    IReadOnlyList<OtherAccountRow> OtherAccounts, bool RefreshInFlight, string? AdviceLine, bool BackLink)
{
    public static PanelModel Empty { get; } = new(null, QuotaView.Initial, null, null, Array.Empty<OtherAccountRow>(), false, null, false);
}

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
    const float LoginButtonHeight = 26f;

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
    // The model being measured / drawn / hit-tested right now. In single mode it is the one model; in full
    // mode the layout, paint and click code point it at each panel in turn (WithModel).
    PanelModel _m = PanelModel.Empty;
    PanelModel _single = PanelModel.Empty;
    QuotaView _view => _m.View;
    bool _backLink => _m.BackLink;
    string? _accountLabel => _m.Label;
    string? _accountSubtitle => _m.Subtitle;
    IReadOnlyList<OtherAccountRow> _otherAccounts => _m.OtherAccounts;
    bool _refreshInFlight => _m.RefreshInFlight;
    string? _adviceLine => _m.AdviceLine;
    bool _demo;

    // ---- full mode (docs/multi-account.md "Panels"): every account's full panel side by side ----
    bool _full;
    IReadOnlyList<PanelModel> _models = Array.Empty<PanelModel>();
    int _hoverPanel = -1;
    float _scrollX;
    const float DividerInset = 10f;        // the thin vertical divider between two columns stops this far from the top and bottom
    const float MarkStripHeight = 3f;      // the accent strip along the top of the clicked account's column
    float _lastHeight = -1f;

    // ---- combined mode (docs/multi-account.md "All accounts in one panel") ----
    bool _combined;
    IReadOnlyList<AccountCard> _cards = Array.Empty<AccountCard>();
    string? _markedSlot;
    int _hoverCard = -1;
    float _scrollY;
    const float CardGap = 8f;
    const float CombinedHeaderHeight = 40f;
    const string AllAccountsTitle = "Alla konton";
    const string BackLinkText = "← Alla konton";
    static readonly Color CardBackground = Color.FromArgb(255, 0x2A, 0x2A, 0x2A);
    static readonly Color CardHover = Color.FromArgb(255, 0x36, 0x36, 0x36);
    static readonly Color CardAccent = Color.FromArgb(255, 0x5B, 0x9B, 0xFF);

    /// <summary>A card was clicked: open that account's detailed panel.</summary>
    public event Action<string>? CardClicked;

    /// <summary>The "Logga in igen" button on a card was clicked.</summary>
    public event Action<string>? CardReloginClicked;

    /// <summary>The "<- Alla konton" link of a detailed panel was clicked.</summary>
    public event Action? BackToAllRequested;
    /// <summary>The header's first line when no account label is given (the single-account demo frames).</summary>
    const string DefaultTitle = "Claude Code";

    /// <summary>The header title is the account's label; the DEMO badge, when shown, takes the right end of that row.</summary>
    string TitleText => _accountLabel ?? DefaultTitle;
    float TitleWidth => ContentWidth - (_demo ? 54f : 0f);

    /// <summary>
    /// docs/multi-account.md "Panel": fired when the user clicks one of the "other accounts"
    /// rows, with that row's AccountIndex -- the caller (StatusBarApplicationContext) owns what
    /// that index means and switches the panel to it.
    /// </summary>
    public event Action<int>? OtherAccountClicked;

    /// <summary>docs/statistics.md decision 4 "Proactive advice": fired when the user clicks the (at most one) advice line, so the caller can open the statistics window.</summary>
    public event Action? AdviceClicked;

    /// <summary>
    /// The task's reload button, header row: fired when the user clicks the refresh control
    /// while it is not already busy. The caller (StatusBarApplicationContext) owns which
    /// account is currently shown and triggers that account's AccountRuntime.
    /// RequestImmediateRefresh -- this form only knows "the user asked to refresh right now",
    /// never which account that means.
    /// </summary>
    public event Action<string?>? RefreshRequested;

    /// <summary>docs/multi-account.md "NeedsLogin": fired when the user clicks the "Logga in igen" button shown under the status box of an account that needs a login. The caller knows which account is shown and starts that account's re-login flow.</summary>
    public event Action<string?>? ReloginRequested;

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
    public void UpdateView(QuotaView view, bool demo, string? accountLabel = null, IReadOnlyList<OtherAccountRow>? otherAccounts = null, bool refreshInFlight = false, string? adviceLine = null, string? accountSubtitle = null, bool showBackLink = false, string? slot = null)
    {
        try
        {
            _combined = false;
            _full = false;
            TransparencyKey = Color.Empty;
            _demo = demo;
            _single = new PanelModel(slot, view, accountLabel, string.IsNullOrWhiteSpace(accountSubtitle) ? null : accountSubtitle,
                otherAccounts ?? Array.Empty<OtherAccountRow>(), refreshInFlight, adviceLine, showBackLink);
            _m = _single;

            RefitIfVisible();
        }
        catch (Exception ex)
        {
            // UI exception boundary (Codex review Medium #15): a layout/measurement failure
            // must not crash the 1Hz eval tick that drives this. The panel keeps showing its
            // last successfully built content until the next tick's attempt succeeds.
            SafeLog.Warn($"PanelForm.UpdateView threw: {ex.Message}");
        }
    }

    /// <summary>
    /// The combined panel: one compact card per account, in the order given. `markedSlot` is the card
    /// of the icon that was clicked (accent edge). Same 1 Hz cadence and exception boundary as UpdateView.
    /// </summary>
    public void UpdateCombined(IReadOnlyList<AccountCard> cards, string? markedSlot, bool demo)
    {
        try
        {
            _combined = true;
            _full = false;
            TransparencyKey = Color.Empty;
            _demo = demo;
            _cards = cards;
            _markedSlot = markedSlot;
            if (_hoverCard >= cards.Count) _hoverCard = -1;
            RefitIfVisible();
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"PanelForm.UpdateCombined threw: {ex.Message}");
        }
    }

    public bool IsCombined => _combined;
    public bool IsFull => _full;

    /// <summary>
    /// Full mode: every account's full panel side by side, in the order given, as ONE solid container of
    /// equal-height columns whose rows line up. `markedSlot` is the panel of the icon that was clicked
    /// (accent strip along its top). The panels
    /// are the single-account rendering exactly, except that the "other accounts" section and the back link
    /// are never shown here: every account is already on screen.
    /// </summary>
    public void UpdateFull(IReadOnlyList<PanelModel> models, string? markedSlot, bool demo)
    {
        try
        {
            _combined = false;
            _full = true;
            TransparencyKey = Color.Empty;
            _demo = demo;
            _models = models.Select(m => m with { OtherAccounts = Array.Empty<OtherAccountRow>(), BackLink = false }).ToList();
            _markedSlot = markedSlot;
            if (_hoverPanel >= _models.Count) _hoverPanel = -1;
            if (_models.Count > 0) _m = _models[0];
            RefitIfVisible();
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"PanelForm.UpdateFull threw: {ex.Message}");
        }
    }

    /// <summary>Index of the panel drawn with the accent edge, or -1.</summary>
    public int MarkedPanelIndex => _models.ToList().FindIndex(m => m.Slot == _markedSlot);

    /// <summary>Natural height of the single-mode panel (tests: single is unchanged and full panels equal it).</summary>
    internal float SingleHeightForTest => WithModel(_single, () => BuildLayout(DateTimeOffset.UtcNow).TotalHeight);

    /// <summary>The slots of the full panels, in drawn order (tests).</summary>
    internal IReadOnlyList<string?> FullPanelSlots => _models.Select(m => m.Slot).ToList();

    /// <summary>Height of every full column (tests): all the same, the tallest column's.</summary>
    internal IReadOnlyList<float> FullPanelHeights => BuildFullLayout().Layouts.Select(l => l.TotalHeight).ToList();

    /// <summary>Each column's block positions (tests: the rows line up across columns).</summary>
    internal record FullRowYs(float StatusBoxY, float StatusBoxHeight, float SessionY, float WeeklyY, float RuleY, float FooterY, float Height);

    internal IReadOnlyList<FullRowYs> FullRows => BuildFullLayout().Layouts
        .Select(l => new FullRowYs(l.StatusBoxY, l.StatusBoxHeight, l.SessionY, l.WeeklyY, l.RuleY, l.FooterY, l.TotalHeight)).ToList();

    /// <summary>x (logical, in the scrolled-off row) of each divider between two columns (tests).</summary>
    internal IReadOnlyList<float> FullDividerXs => BuildFullLayout().Xs.Skip(1).ToList();

    /// <summary>The accent strip of the marked column, in the row's own coordinates, or null (tests).</summary>
    internal RectangleF? FullMarkStrip => MarkedPanelIndex is >= 0 and var i ? new RectangleF(BuildFullLayout().Xs[i], 0f, LogicalWidth, MarkStripHeight) : null;

    /// <summary>Is this screen point inside the panel (the whole container, gaps and all, for dismissal)?</summary>
    internal bool IsInsidePanelsForTest(Point screenPoint) => IsInsidePanels(screenPoint);

    /// <summary>Demo captures: show the panel of this slot as if the mouse were over it.</summary>
    internal void SetHoverPanelForDemo(string? slot) => _hoverPanel = _models.ToList().FindIndex(m => m.Slot == slot);

    /// <summary>Runs `work` with the model pointer on `model`, then puts it back.</summary>
    T WithModel<T>(PanelModel model, Func<T> work)
    {
        PanelModel previous = _m;
        _m = model;
        try { return work(); }
        finally { _m = previous; }
    }

    /// <summary>The columns of the full container: x offsets, one aligned layout each (all the same height), the total width.</summary>
    readonly record struct FullLayout(IReadOnlyList<float> Xs, IReadOnlyList<PanelLayout> Layouts, float TotalWidth, float Height);

    /// <summary>
    /// Two passes over the columns, both through the ordinary single-panel BuildLayout: the first measures
    /// every column's blocks (header, status box, session, week, content), the second lays each column out
    /// again with every block at least as tall as the tallest column's -- so the status boxes, AKTUELL
    /// SESSION, VECKA, the rule and the footer start at the same y in every column, and every column is
    /// as tall as the tallest. The columns touch: one container, no gaps.
    /// </summary>
    FullLayout BuildFullLayout()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var natural = _models.Select(m => WithModel(m, () => BuildLayout(now))).ToList();
        RowMarks? align = natural.Count == 0 ? null : new RowMarks(
            natural.Max(l => l.StatusBoxY),
            natural.Max(l => l.StatusBoxHeight),
            natural.Max(l => l.SessionY - l.StatusBoxY - l.StatusBoxHeight),
            natural.Max(l => l.SessionHeight),
            natural.Max(l => l.WeeklyHeight));

        var aligned = new List<PanelLayout>();
        foreach (PanelModel model in _models)
            aligned.Add(WithModel(model, () => BuildLayout(now, align)));
        float height = aligned.Count == 0 ? 0f : aligned.Max(l => l.TotalHeight);

        var xs = new List<float>();
        var layouts = new List<PanelLayout>();
        float x = 0f;
        foreach (PanelLayout layout in aligned)
        {
            layouts.Add(layout with { TotalHeight = height }); // every column as tall as the tallest
            xs.Add(x);
            x += LogicalWidth;
        }
        return new FullLayout(xs, layouts, Math.Max(LogicalWidth, x), height);
    }

    /// <summary>The visible height in logical px (the window's, once it has one); the container sits on its bottom edge.</summary>
    float FullViewHeight(FullLayout layout) => _scale > 0f && Height > 1 ? Height / _scale : layout.Height;

    /// <summary>Which full panel a logical x (window coordinates) is over, or -1.</summary>
    int FullIndexAt(FullLayout layout, float logicalX)
    {
        float cx = logicalX + _scrollX;
        for (int i = 0; i < layout.Xs.Count; i++)
            if (cx >= layout.Xs[i] && cx < layout.Xs[i] + LogicalWidth) return i;
        return -1;
    }

    /// <summary>Inside the panel for dismissal: the whole window -- the container is solid, a click anywhere in it (dividers included) is a click in the panel.</summary>
    bool IsInsidePanels(Point screenPoint) => Bounds.Contains(screenPoint);

    float ClampScrollX(float value) => Math.Clamp(value, 0f, Math.Max(0f, BuildFullLayout().TotalWidth - Width / _scale));

    /// <summary>On opening, scroll a row that does not fit so that the marked panel is in view.</summary>
    void ScrollMarkedIntoView()
    {
        if (!_full) { _scrollX = 0f; return; }
        FullLayout layout = BuildFullLayout();
        int marked = MarkedPanelIndex;
        float centre = marked >= 0 ? layout.Xs[marked] + LogicalWidth / 2f : layout.TotalWidth;
        _scrollX = ClampScrollX(centre - Width / _scale / 2f);
    }

    /// <summary>Index of the card drawn with the accent edge (the clicked icon's), or -1.</summary>
    public int MarkedCardIndex => _cards.ToList().FindIndex(c => c.Slot == _markedSlot);

    /// <summary>Card rectangles in content coordinates (tests: ordering and that every account has one).</summary>
    internal IReadOnlyList<RectangleF> CardRectsForTest => BuildCombinedLayout().CardRects;

    void RefitIfVisible()
    {
        if (!Visible) return;
        (float width, float height) = MeasureContent(DateTimeOffset.UtcNow);
        if (Math.Abs(height - _lastHeight) > 0.5f || Math.Abs(width - _lastWidth) > 0.5f) Reanchor();
        else Invalidate();
    }

    float _lastWidth = LogicalWidth;

    /// <summary>The content size of whichever view is showing (before the work-area cap).</summary>
    (float Width, float Height) MeasureContent(DateTimeOffset now)
    {
        if (_full) { FullLayout f = BuildFullLayout(); return (f.TotalWidth, f.Height); }
        return (LogicalWidth, _combined ? BuildCombinedLayout().TotalHeight : BuildLayout(now).TotalHeight);
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

            if (_combined)
            {
                CombinedLayout combinedLayout = BuildCombinedLayout();
                var contentPoint = new PointF(logicalPoint.X, logicalPoint.Y + _scrollY);
                if (logicalPoint.Y < CombinedHeaderHeight) return; // the fixed header: nothing to click
                for (int i = 0; i < combinedLayout.CardRects.Count; i++)
                {
                    if (!combinedLayout.CardRects[i].Contains(contentPoint)) continue;
                    if (_cards[i].OfferRelogin && combinedLayout.ReloginRects[i].Contains(contentPoint)) CardReloginClicked?.Invoke(_cards[i].Slot);
                    else CardClicked?.Invoke(_cards[i].Slot);
                    return;
                }
                return;
            }

            PanelLayout layout;
            if (_full)
            {
                // Find the panel under the click, then run the ordinary single-panel hit test on it
                // with the point translated into that panel's own coordinates.
                FullLayout full = BuildFullLayout();
                int i = FullIndexAt(full, logicalPoint.X);
                if (i < 0) return;
                float top = FullViewHeight(full) - full.Height;
                logicalPoint = new PointF(logicalPoint.X + _scrollX - full.Xs[i], logicalPoint.Y - top);
                _m = _models[i];
                layout = full.Layouts[i];
            }
            else
            {
                layout = BuildLayout(DateTimeOffset.UtcNow);
            }

            if (_backLink && layout.BackLinkRect.Contains(logicalPoint))
            {
                BackToAllRequested?.Invoke();
                return;
            }

            // Checked first regardless of _otherAccounts: the reload button lives in the header,
            // well above the other-accounts rows, so there is no coordinate overlap to arbitrate.
            // Ignored while already busy -- this is the actual guard against a click starting a
            // second concurrent poll for the shown account (StatusBarApplicationContext.IsPolling
            // is the other half, for the timer-driven case).
            if (!_refreshInFlight && layout.RefreshHitRect.Contains(logicalPoint))
            {
                RefreshRequested?.Invoke(_m.Slot);
                return;
            }

            if (_view.NeedsLogin && layout.LoginButtonRect.Contains(logicalPoint))
            {
                ReloginRequested?.Invoke(_m.Slot);
                return;
            }

            if (_adviceLine != null && layout.AdviceHitRect.Contains(logicalPoint))
            {
                AdviceClicked?.Invoke();
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

    /// <summary>The card under the mouse is highlighted.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_full && _scale > 0f)
        {
            int hoverPanel = FullIndexAt(BuildFullLayout(), e.X / _scale);
            if (hoverPanel != _hoverPanel) { _hoverPanel = hoverPanel; Invalidate(); }
            return;
        }
        if (!_combined || _scale <= 0f) return;
        try
        {
            CombinedLayout layout = BuildCombinedLayout();
            var p = new PointF(e.X / _scale, e.Y / _scale + _scrollY);
            int hover = e.Y / _scale < CombinedHeaderHeight ? -1 : layout.CardRects.ToList().FindIndex(r => r.Contains(p));
            if (hover != _hoverCard) { _hoverCard = hover; Invalidate(); }
        }
        catch (Exception ex) { SafeLog.Warn($"PanelForm.OnMouseMove threw: {ex.Message}"); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCard != -1 || _hoverPanel != -1) { _hoverCard = -1; _hoverPanel = -1; Invalidate(); }
    }

    /// <summary>Scrolls the combined panel when its cards do not fit the work area.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_full && _scale > 0f)
        {
            // Plain wheel and Shift+wheel both scroll the row: the panels have no vertical scroll.
            _scrollX = ClampScrollX(_scrollX - e.Delta / 120f * 80f);
            Invalidate();
            return;
        }
        if (!_combined || _scale <= 0f) return;
        _scrollY = ClampScroll(_scrollY - e.Delta / 120f * 40f);
        Invalidate();
    }

    float ClampScroll(float value)
    {
        float max = Math.Max(0f, BuildCombinedLayout().TotalHeight - Height / _scale);
        return Math.Clamp(value, 0f, max);
    }

    public void ShowPanel()
    {
        if (Visible) return;

        (_lastWidth, _lastHeight) = MeasureContent(DateTimeOffset.UtcNow);
        var (location, size, scale) = PanelAnchor.Resolve(_trayIcon, new Size((int)Math.Ceiling(_lastWidth), (int)Math.Ceiling(_lastHeight)), _full ? LogicalWidth : 0);
        _scale = scale;
        Size = size;
        Location = location;
        ScrollMarkedIntoView();

        Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE: never takes focus or foreground
        _tickTimer.Start();

        // The tray icon's own rectangle is excluded from click-away dismissal (Codex review
        // Medium #13): otherwise a click ON the tray icon to close the panel lands as
        // "outside the panel" here, hides it, and then the tray's own MouseClick handler
        // (StatusBarApplicationContext) sees a hidden panel and reopens it -- one click that
        // should close the panel instead leaves it open.
        _dismissWatcher.Start(
            IsInsidePanels,
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
        (_lastWidth, _lastHeight) = MeasureContent(DateTimeOffset.UtcNow);
        var (location, size, scale) = PanelAnchor.Resolve(_trayIcon, new Size((int)Math.Ceiling(_lastWidth), (int)Math.Ceiling(_lastHeight)), _full ? LogicalWidth : 0);
        _scale = scale;
        Bounds = new Rectangle(location, size);
        if (_combined) _scrollY = ClampScroll(_scrollY);
        if (_full) _scrollX = ClampScrollX(_scrollX);
        Invalidate();
    }

    // ---- layout: single source of truth for every section's Y-offset (and, via DrawFitText, every wrap decision) ----

    readonly record struct PanelLayout(
        PanelTextResult Text, float SubtitleY, float FreshnessY, float StatusBoxY, float StatusBoxHeight,
        float SessionY, float SessionHeight, float WeeklyY, float WeeklyHeight,
        float RuleY, float FooterY, float AdviceY, float AdviceHeight, RectangleF AdviceHitRect, RectangleF BackLinkRect,
        float OtherAccountsRuleY, float OtherAccountsY,
        IReadOnlyList<RectangleF> OtherAccountRowRects, RectangleF RefreshHitRect, RectangleF LoginButtonRect, float TotalHeight);

    /// <summary>
    /// What the full container asks of every column so the rows line up like a table: the y where the
    /// status box starts, and the (tallest column's) height of the status box, of the space between it and
    /// AKTUELL SESSION (login button and margin), and of the two sections. Null/absent in the single panel.
    /// </summary>
    readonly record struct RowMarks(float StatusBoxY, float StatusBoxHeight, float StatusExtra, float SessionHeight, float WeeklyHeight);

    float ContentWidth => LogicalWidth - 2 * SidePadding;
    /// <summary>The "<- Alla konton" row above the title in a detailed panel opened from the combined one.</summary>
    float HeaderOffset => _backLink ? 18f : 0f;

    PanelLayout BuildLayout(DateTimeOffset now, RowMarks? align = null)
    {
        PanelTextResult text = PanelText.Compose(_view, now, TimeZoneInfo.Local);

        float y = 12f + HeaderOffset;
        var backLinkRect = _backLink ? new RectangleF(SidePadding, 10f, 110f, 16f) : RectangleF.Empty;
        // Title: the account's label (its own name or the automatic one). It may shrink or wrap, so it
        // is measured like everything else rather than assumed to be one 20px line.
        y += Math.Max(20f, DrawFitText(_measureG, TitleText, _fontTitle, TextPrimary, 0, 0, TitleWidth, draw: false));

        // Subtitle: plan and organisation from Anthropic data; absent when unknown or when it would
        // just repeat the title (PanelText.ComposeAccountSubtitle).
        float subtitleY = -1f;
        if (_accountSubtitle is { } subtitle)
        {
            subtitleY = y;
            y += DrawFitText(_measureG, subtitle, _fontResetHeader, TextSecondary, 0, 0, ContentWidth, draw: false) + 4f;
        }

        const float freshnessRowHeight = 22f;
        if (align is { } rows) y = Math.Max(y, rows.StatusBoxY - freshnessRowHeight); // the freshness row stays attached to the status box; the slack sits above it
        float freshnessY = y;
        var refreshHitRect = new RectangleF(
            LogicalWidth - SidePadding - RefreshHitSize,
            freshnessY + (freshnessRowHeight - RefreshHitSize) / 2f,
            RefreshHitSize, RefreshHitSize);
        y += freshnessRowHeight;

        float statusBoxY = y;
        float statusBoxHeight = Math.Max(MeasureStatusBox(text.StatusBox), align?.StatusBoxHeight ?? 0f);
        y += statusBoxHeight;

        var loginButtonRect = RectangleF.Empty;
        if (_view.NeedsLogin)
        {
            y += 8f;
            loginButtonRect = new RectangleF(SidePadding, y, ContentWidth, LoginButtonHeight);
            y += LoginButtonHeight;
        }
        y += 14f;
        if (align is { } block) y = Math.Max(y, statusBoxY + block.StatusBoxHeight + block.StatusExtra);

        float sessionY = y;
        float sessionHeight = Math.Max(MeasureSection(text.Session, _view.Session), align?.SessionHeight ?? 0f);
        y += sessionHeight + 16f;

        float weeklyY = y;
        float weeklyHeight = Math.Max(MeasureSection(text.Weekly, _view.Weekly), align?.WeeklyHeight ?? 0f);
        y += weeklyHeight + 14f;

        float ruleY = y;
        y += 12f;
        float footerY = y;
        y += 20f;

        float adviceY = -1f;
        float adviceHeight = 0f;
        var adviceHitRect = RectangleF.Empty;
        if (_adviceLine is { } advice)
        {
            y += 6f;
            adviceY = y;
            adviceHeight = DrawFitText(_measureG, advice, _fontBarLabel, TextPrimary, 0, 0, ContentWidth - 16f, draw: false) + 12f;
            adviceHitRect = new RectangleF(SidePadding, adviceY, ContentWidth, adviceHeight);
            y += adviceHeight + 4f;
        }

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

        return new PanelLayout(text, subtitleY, freshnessY, statusBoxY, statusBoxHeight, sessionY, sessionHeight,
            weeklyY, weeklyHeight, ruleY, footerY, adviceY, adviceHeight, adviceHitRect, backLinkRect,
            otherAccountsRuleY, otherAccountsY, rowRects, refreshHitRect, loginButtonRect, y);
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
            // One background and one outer frame for every mode: in full mode the whole row of columns is a
            // single container (DrawFull only adds the dividers and the marks).
            g.Clear(PanelBackground);
            using (var border = new Pen(BorderColor))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(_scale, _scale); // everything from here on is in logical (96-DPI) px

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_combined)
            {
                DrawCombined(g);
                return;
            }
            if (_full)
            {
                DrawFull(g, now);
                return;
            }
            PanelLayout layout = BuildLayout(now);
            DrawPanelContents(g, layout, now);
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

    /// <summary>Everything inside one panel's rectangle, in that panel's own coordinates. The ONE drawing path of both single and full mode.</summary>
    void DrawPanelContents(Graphics g, PanelLayout layout, DateTimeOffset now)
    {
        DrawHeader(g, layout);
        DrawStatusBox(g, layout);
        DrawLoginButton(g, layout);
        DrawSection(g, layout.SessionY, layout.Text.Session, _view.Session, now);
        DrawSection(g, layout.WeeklyY, layout.Text.Weekly, _view.Weekly, now);

        using (var rulePen = new Pen(RuleColor))
            g.DrawLine(rulePen, SidePadding, layout.RuleY, LogicalWidth - SidePadding, layout.RuleY);

        using (var footerBrush = new SolidBrush(TextTertiary))
            g.DrawString(BuildFooter(_view), _fontFooter, footerBrush, SidePadding, layout.FooterY);

        DrawAdvice(g, layout);
        DrawOtherAccounts(g, layout);
    }

    /// <summary>
    /// Full mode: ONE container (the form's own background and border) holding a column per account. Each
    /// column is the ordinary single-panel rendering (DrawPanelContents) at its x offset, on the aligned
    /// layout of BuildFullLayout; a 1 px divider in the border colour, inset from the top and bottom, sits
    /// between two columns. The clicked account's column has an accent strip along its top edge, the
    /// column under the mouse a faint lift. The content scrolled out of the window is clipped cleanly by
    /// the frame.
    /// </summary>
    void DrawFull(Graphics g, DateTimeOffset now)
    {
        FullLayout full = BuildFullLayout();
        float viewH = FullViewHeight(full);
        float top = viewH - full.Height; // normally 0; negative when the work area caps the window (the container sits on the bottom edge)

        var clipState = g.Save();
        g.SetClip(new RectangleF(1, 1, Width / _scale - 2, Height / _scale - 2));
        g.TranslateTransform(-_scrollX, top);

        for (int i = 0; i < _models.Count; i++)
        {
            var colState = g.Save();
            g.TranslateTransform(full.Xs[i], 0);
            PanelModel model = _models[i];

            if (i == _hoverPanel)
            {
                SmoothingMode smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using var lift = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
                g.FillRectangle(lift, 0, 0, LogicalWidth, full.Height);
                g.SmoothingMode = smoothing;
            }

            WithModel(model, () => { DrawPanelContents(g, full.Layouts[i], now); return 0; });

            if (model.Slot is not null && model.Slot == _markedSlot)
            {
                SmoothingMode smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using var accent = new SolidBrush(CardAccent);
                g.FillRectangle(accent, 0, 0, LogicalWidth, MarkStripHeight);
                g.SmoothingMode = smoothing;
            }
            g.Restore(colState);
        }

        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var divider = new Pen(BorderColor, 1f))
            foreach (float x in full.Xs.Skip(1))
                g.DrawLine(divider, x, DividerInset, x, full.Height - DividerInset);
        g.SmoothingMode = sm;
        g.Restore(clipState);
    }


    // ---- the combined panel ----

    readonly record struct CombinedLayout(IReadOnlyList<RectangleF> CardRects, IReadOnlyList<RectangleF> ReloginRects, IReadOnlyList<float> CardHeights, float TotalHeight);

    const float CardPad = 10f;
    const float CardBarCaptionWidth = 46f;
    const float CardBarWidth = 76f;
    const float CardRowHeight = 16f;

    float CardInnerWidth => ContentWidth - 2 * CardPad;

    CombinedLayout BuildCombinedLayout()
    {
        var rects = new List<RectangleF>();
        var relogin = new List<RectangleF>();
        var heights = new List<float>();
        float y = CombinedHeaderHeight;
        foreach (AccountCard card in _cards)
        {
            float h = CardPad + CardRowHeight + (card.Subtitle is null ? 0f : 14f) + 4f + CardRowHeight + 4f + 2 * CardRowHeight
                + (card.Note is null ? 0f : 4f + 18f) + CardPad;
            var rect = new RectangleF(SidePadding, y, ContentWidth, h);
            rects.Add(rect);
            relogin.Add(card.OfferRelogin
                ? new RectangleF(rect.Right - CardPad - 96f, rect.Bottom - CardPad - 18f, 96f, 18f)
                : RectangleF.Empty);
            heights.Add(h);
            y += h + CardGap;
        }
        return new CombinedLayout(rects, relogin, heights, y + 4f);
    }

    void DrawCombined(Graphics g)
    {
        CombinedLayout layout = BuildCombinedLayout();

        var state = g.Save();
        g.TranslateTransform(0, -_scrollY);
        for (int i = 0; i < _cards.Count; i++) DrawCard(g, _cards[i], layout.CardRects[i], layout.ReloginRects[i], hover: i == _hoverCard, marked: _cards[i].Slot == _markedSlot);
        g.Restore(state);

        // The fixed header, painted over whatever scrolled beneath it.
        using (var bg = new SolidBrush(PanelBackground)) g.FillRectangle(bg, 1, 1, LogicalWidth - 2, CombinedHeaderHeight - 1);
        DrawFitText(g, AllAccountsTitle, _fontTitle, TextPrimary, SidePadding, 12f, TitleWidth);
        if (_demo)
        {
            const float badgeW = 46f, badgeH = 16f;
            var badgeRect = new RectangleF(LogicalWidth - SidePadding - badgeW, 13f, badgeW, badgeH);
            using (var path = RoundedRect(badgeRect, 4f))
            using (var bgb = new SolidBrush(DemoBadgeBg)) g.FillPath(bgb, path);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Color.Black);
            g.DrawString("DEMO", _fontDemoBadge, textBrush, badgeRect, fmt);
        }
    }

    void DrawCard(Graphics g, AccountCard card, RectangleF rect, RectangleF reloginRect, bool hover, bool marked)
    {
        using (var path = RoundedRect(rect, 6f))
        using (var bg = new SolidBrush(hover ? CardHover : CardBackground))
            g.FillPath(bg, path);
        if (marked)
        {
            using var accent = new SolidBrush(CardAccent);
            g.FillRectangle(accent, rect.X, rect.Y + 4f, 3f, rect.Height - 8f);
        }

        float x = rect.X + CardPad + 2f;
        float w = rect.Width - 2 * CardPad - 2f;
        float y = rect.Y + CardPad;

        DrawFitText(g, card.Title, _fontSectionTitle, TextPrimary, x, y, w);
        y += CardRowHeight;
        if (card.Subtitle is { } sub)
        {
            DrawFitText(g, sub, _fontResetHeader, TextSecondary, x, y, w);
            y += 14f;
        }
        y += 4f;

        DrawFitText(g, card.Verdict, _fontStatusLine2, RoleColor(card.VerdictRole), x, y, w);
        y += CardRowHeight + 4f;

        DrawCardBar(g, card.Session, x, y, w);
        y += CardRowHeight;
        DrawCardBar(g, card.Week, x, y, w);
        y += CardRowHeight;

        if (card.Note is { } note)
        {
            y += 4f;
            float noteWidth = card.OfferRelogin ? w - 104f : w;
            DrawFitText(g, note, _fontStatusLine3, RoleColor(card.NoteRole), x, y + 2f, noteWidth);
            if (card.OfferRelogin)
            {
                using var path = RoundedRect(reloginRect, 5f);
                using var bg = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
                using var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
                g.FillPath(bg, path);
                g.DrawPath(edge, path);
                using var tb = new SolidBrush(TextPrimary);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(LoginText.ReloginButton, _fontStatusLine3, tb, reloginRect, fmt);
            }
        }
    }

    void DrawCardBar(Graphics g, CardBar bar, float x, float y, float w)
    {
        using (var capBrush = new SolidBrush(TextTertiary))
            g.DrawString(bar.Caption, _fontBarCaption, capBrush, x, y + 2f);

        var track = new RectangleF(x + CardBarCaptionWidth, y + 5f, CardBarWidth, 5f);
        FillBarTrack(g, track);
        if (bar.Fraction > 0)
        {
            using var fill = new SolidBrush(RoleColor(bar.Role));
            g.FillRectangle(fill, track.X, track.Y, (float)bar.Fraction * track.Width, track.Height);
        }

        float textX = track.Right + 8f;
        DrawFitText(g, bar.Text, _fontBarLabel, TextSecondary, textX, y, x + w - textX);
    }

    void DrawHeader(Graphics g, PanelLayout layout)
    {
        if (_backLink)
            DrawFitText(g, BackLinkText, _fontFreshness, TextSecondary, SidePadding, 10f, 110f);

        DrawFitText(g, TitleText, _fontTitle, TextPrimary, SidePadding, 12f + HeaderOffset, TitleWidth);

        if (_demo)
        {
            const float badgeW = 46f, badgeH = 16f;
            var badgeRect = new RectangleF(LogicalWidth - SidePadding - badgeW, 13f + HeaderOffset, badgeW, badgeH);
            using (var path = RoundedRect(badgeRect, 4f))
            using (var bg = new SolidBrush(DemoBadgeBg))
                g.FillPath(bg, path);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Color.Black);
            g.DrawString("DEMO", _fontDemoBadge, textBrush, badgeRect, fmt);
        }

        if (_accountSubtitle is { } subtitle)
            DrawFitText(g, subtitle, _fontResetHeader, TextSecondary, SidePadding, layout.SubtitleY, ContentWidth);

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
    /// docs/statistics.md decision 4 "Proactive advice": at most one clickable line, only when
    /// StatusBarApplicationContext decided (Model/StatisticsAdvice.Decide) that a recommendation
    /// just cleared its threshold or changed since it was last shown. A small rounded chip so it
    /// reads as a distinct, actionable element rather than another status line -- clicking it
    /// opens the statistics window (AdviceClicked), never shown at all otherwise.
    /// </summary>
    void DrawAdvice(Graphics g, PanelLayout layout)
    {
        if (_adviceLine is not { } advice) return;

        Color accent = Palette.Ok;
        using (var path = RoundedRect(layout.AdviceHitRect, 6f))
        using (var bg = new SolidBrush(Color.FromArgb(20, accent)))
            g.FillPath(bg, path);
        using (var edge = new SolidBrush(accent))
            g.FillRectangle(edge, layout.AdviceHitRect.X, layout.AdviceHitRect.Y, 3f, layout.AdviceHitRect.Height);

        DrawFitText(g, advice, _fontBarLabel, TextPrimary, layout.AdviceHitRect.X + 12f, layout.AdviceY + 6f, layout.AdviceHitRect.Width - 20f);
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

    /// <summary>
    /// docs/multi-account.md "NeedsLogin": a plain painted button (never a real WinForms control -- the panel
    /// must stay non-activating) under the status box; OnMouseDown hit-tests the same rectangle.
    /// </summary>
    void DrawLoginButton(Graphics g, PanelLayout layout)
    {
        if (!_view.NeedsLogin) return;
        RectangleF rect = layout.LoginButtonRect;
        using (var path = RoundedRect(rect, 6f))
        {
            using var bg = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
            using var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            g.FillPath(bg, path);
            g.DrawPath(edge, path);
        }
        using var textBrush = new SolidBrush(TextPrimary);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(LoginText.ReloginButton, _fontStatusLine2, textBrush, rect, fmt);
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
        if (view.Loading) return ("Hämtar…", TextSecondary);
        if (view.NeedsLogin) return ("Inte inloggad", Palette.Crit);
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
        view.LastSuccessAt is { } lastPoll
            ? $"Senast avläst {TimeText.ClockWithSecondsAndDay(lastPoll, DateTimeOffset.UtcNow, TimeZoneInfo.Local)} · uppdateras var {TimeText.Duration(view.PollInterval)}"
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

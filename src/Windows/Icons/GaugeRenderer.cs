using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// Renders the gauge glyph into a Bitmap. Ported near-verbatim from the measured
/// probe (scratchpad/icontest/Program.cs, GaugeRenderer) which validated the
/// supersample + HighQualityBicubic downsample pipeline and the transparent-gap
/// erase trick. Geometry matches scratchpad/design/icons.js (outer ring = weekly
/// "all models", inner pie = 5h session, forecast wedge protrudes radially past
/// the consumed arc/pie).
///
/// RenderQuota (docs/forecast-and-states.md, "Icon") is the entry point everything
/// live/demo goes through; Render/RenderRing below are the lower-level primitives
/// it (and the panel's card rings) build on.
/// </summary>
public static class GaugeRenderer
{
    const int SS = 16; // supersample factor (box-filtered down, see Downsample)

    /// <summary>
    /// px                  output side in pixels (16/20/24/32)
    /// ringFrac            weekly "all models" utilization, 0-1, fills the outer ring clockwise from 12
    /// pieFrac             5h session utilization, 0-1 -- OR, when exhausted, the countdown-to-reset
    ///                     fraction remaining (drains toward 0 as reset approaches)
    /// forecastFrac        weekly projected utilization at reset, 0-1; ring wedge continues past ringFrac to here
    /// sessionForecastFrac session projected utilization at reset, 0-1; inner-pie sector continues past pieFrac to here
    /// ring/pie            colours for the ring arc / inner pie (caller resolves via Palette)
    /// exhausted           true once the weekly limit is fully consumed: ring becomes a dashed outline,
    ///                     the whole glyph is dimmed, and pieFrac is reinterpreted as time-left
    /// dimAlpha            brightness matrix applied in the exhausted state; measured targets are
    ///                     0.85 on a dark taskbar and 0.42 on a light one (see Palette/taskbar theme)
    /// taskbarDark         picks the session forecast sector's outline colour (dark outline on a
    ///                     dark taskbar, light outline on a light taskbar) so it keeps reading as
    ///                     a distinct "projected, not consumed" edge against either background
    /// </summary>
    public static Bitmap Render(
        int px,
        double ringFrac,
        double pieFrac,
        double forecastFrac,
        double sessionForecastFrac,
        Color ring,
        Color pie,
        bool exhausted,
        bool taskbarDark,
        float dimAlpha = 0.42f)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            var (band, rOuter, ringRect) = RingGeometry(s);

            using (var trackPen = new Pen(TrackColor(exhausted ? 40 : 70, taskbarDark), band))
                g.DrawArc(trackPen, ringRect, 0, 360);

            // forecast wedge: wider band, protrudes radially, dark outline.
            // Below 20px the band is only ~2.5px -- not legible, so it is dropped.
            if (forecastFrac > ringFrac && !exhausted && px >= 20)
            {
                float wband = band * 1.55f;
                using var fp = new Pen(Color.FromArgb(96, ring), wband);
                using var op = new Pen(Color.FromArgb(220, 10, 10, 12), Math.Max(1f, s * 0.012f));
                float a0 = -90f + (float)(ringFrac * 360.0);
                float sweep = (float)((Math.Min(1.0, forecastFrac) - ringFrac) * 360.0);
                g.DrawArc(fp, ringRect, a0, sweep);
                var outer = new RectangleF(ringRect.X - wband / 2, ringRect.Y - wband / 2, ringRect.Width + wband, ringRect.Height + wband);
                g.DrawArc(op, outer, a0, sweep);
            }

            // consumed ring arc, clockwise from 12
            if (exhausted)
            {
                // Full colour alpha here: the exhausted state's ONLY brightness control is
                // the ColorMatrix dimAlpha applied at final composite below. An extra fixed
                // pen alpha on top of that (previously 200/255) stacks multiplicatively and
                // was measured to drop the dashed ring to ~3.6:1 contrast on #202020 at 16px,
                // below the required 5:1 -- see IconContrastTests.
                using var dash = new Pen(ring, band * 0.92f) { DashStyle = DashStyle.Custom, DashPattern = new[] { 2.2f, 2.0f } };
                g.DrawArc(dash, ringRect, 0, 360);
            }
            else if (ringFrac > 0)
            {
                using var rp = new Pen(ring, band);
                g.DrawArc(rp, ringRect, -90f, (float)(ringFrac * 360.0));
            }

            // genuinely transparent gap: punch an annulus down to alpha 0
            float gap = band * 0.42f;
            float rPieOuter = rOuter - band * 0.5f - gap;
            using (var erase = new SolidBrush(Color.Transparent))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                float rk = rPieOuter + gap;
                g.FillEllipse(erase, s * 0.5f - rk, s * 0.5f - rk, rk * 2, rk * 2);
                g.CompositingMode = CompositingMode.SourceOver;
            }

            // inner pie: session utilization, or (exhausted) time-left countdown
            var pr = new RectangleF(s * 0.5f - rPieOuter, s * 0.5f - rPieOuter, rPieOuter * 2, rPieOuter * 2);
            if (pieFrac > 0)
            {
                using var pb = new SolidBrush(pie);
                g.FillPie(pb, pr, -90f, (float)(Math.Min(1.0, pieFrac) * 360.0));
            }

            // session forecast sector: continues clockwise from the consumed sector to
            // min(ProjectedPctAtReset, 100), same session state colour at reduced alpha with
            // a thin outline, so it reads as "projected, not consumed" on the inner pie
            // itself. Unlike the ring wedge above, drawn at every icon size (including 16px)
            // per the product requirement -- it is the only forecast visible at the user's
            // actual tray size, since the outer ring wedge is dropped below 20px.
            if (!exhausted && sessionForecastFrac > pieFrac)
            {
                float fa0 = -90f + (float)(pieFrac * 360.0);
                float fsweep = (float)((Math.Min(1.0, sessionForecastFrac) - pieFrac) * 360.0);
                using var sfBrush = new SolidBrush(Color.FromArgb(112, pie)); // ~44% alpha
                g.FillPie(sfBrush, pr, fa0, fsweep);

                Color outline = taskbarDark ? Color.FromArgb(220, 10, 10, 12) : Color.FromArgb(220, 245, 245, 245);
                using var sfPen = new Pen(outline, Math.Max(1f, s * 0.012f));
                g.DrawPie(sfPen, pr, fa0, fsweep);
            }
        }

        return Downsample(hi, px, exhausted, dimAlpha);
    }

    /// <summary>
    /// Renders a single ring (track + one consumed arc, optional forecast wedge, no
    /// inner pie) at the given side length. Used by the panel's two big per-metric
    /// gauges (session, weekly) -- it shares RingGeometry/the supersample pipeline
    /// with Render() so the band width and radius can never drift between the
    /// tray icon and the panel even though the panel draws only one metric per
    /// ring instead of the icon's nested ring+pie.
    /// </summary>
    public static Bitmap RenderRing(
        int px,
        double frac,
        Color color,
        bool exhausted,
        double? forecastFrac = null,
        float dimAlpha = 0.42f)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            var (band, _, ringRect) = RingGeometry(s);

            using (var trackPen = new Pen(Color.FromArgb(exhausted ? 40 : 70, 255, 255, 255), band))
                g.DrawArc(trackPen, ringRect, 0, 360);

            if (forecastFrac is { } ff && ff > frac && !exhausted && px >= 20)
            {
                float wband = band * 1.55f;
                using var fp = new Pen(Color.FromArgb(96, color), wband);
                using var op = new Pen(Color.FromArgb(220, 10, 10, 12), Math.Max(1f, s * 0.012f));
                float a0 = -90f + (float)(frac * 360.0);
                float sweep = (float)((Math.Min(1.0, ff) - frac) * 360.0);
                g.DrawArc(fp, ringRect, a0, sweep);
                var outer = new RectangleF(ringRect.X - wband / 2, ringRect.Y - wband / 2, ringRect.Width + wband, ringRect.Height + wband);
                g.DrawArc(op, outer, a0, sweep);
            }

            if (exhausted)
            {
                using var dash = new Pen(color, band * 0.92f) { DashStyle = DashStyle.Custom, DashPattern = new[] { 2.2f, 2.0f } };
                g.DrawArc(dash, ringRect, 0, 360);
            }
            else if (frac > 0)
            {
                using var rp = new Pen(color, band);
                g.DrawArc(rp, ringRect, -90f, (float)(Math.Min(1.0, frac) * 360.0));
            }
        }

        return Downsample(hi, px, exhausted, dimAlpha);
    }

    /// <summary>
    /// The "can't show a quota" glyph: the grey ring with a mark in its centre -- "!" for a real read
    /// failure (Freshness.Unknown), a padlock for "not logged in" (the zero-accounts icon, NeedsLogin).
    /// Everything is drawn antialiased at SS times the output size and box-filtered down once, with the
    /// mark's geometry sitting on the OUTPUT pixel grid (so a straight edge stays a crisp edge and only
    /// curves are softened), at every tray size (16/20/24/32).
    /// </summary>
    public static Bitmap RenderOutline(int px, bool padlock = false, bool taskbarDark = true)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            ConfigureHiRes(g);
            var (band, _, ringRect) = RingGeometry(s);
            Color grey = Palette.ForTaskbar(Palette.Dead, taskbarDark, 0.92);
            using var pen = new Pen(Color.FromArgb(170, grey), band * 0.45f);
            g.DrawEllipse(pen, ringRect);

            if (padlock) DrawPadlockHi(g, px, grey);
            else DrawExclamationHi(g, px, grey);
        }
        return Downsample(hi, px, exhausted: false, dimAlpha: 1f);
    }

    /// <summary>How much the exhausted glyph is quieted: its alpha ceiling. The same on both taskbars (the light one gets a darker red instead, Palette.ForTaskbar).</summary>
    public const float ExhaustedDimAlpha = 0.85f;

    /// <summary>The ring's empty track: translucent white on a dark taskbar, translucent black on a light one (white would vanish there).</summary>
    static Color TrackColor(int alpha, bool taskbarDark) =>
        taskbarDark ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb((int)(alpha * 0.75), 0, 0, 0);

    /// <summary>Antialiased, half-pixel offset: coordinates are exact positions on the (hi-res) pixel grid.</summary>
    static void ConfigureHiRes(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
    }

    /// <summary>The "!": a rounded bar over a dot, on the output pixel grid, as wide as the dot (even widths, so it is centred exactly).</summary>
    static void DrawExclamationHi(Graphics g, int px, Color colour)
    {
        float d = px <= 24 ? 2f : 4f;                                        // bar width = dot diameter, in output px
        float barH = MathF.Max(3f, MathF.Round(px * 0.33f));
        float gap = MathF.Max(1f, MathF.Round(px * 0.07f));
        float totalH = barH + gap + d;
        float top = MathF.Round((px - totalH) / 2f);
        float left = (px - d) / 2f;

        using var brush = new SolidBrush(Color.FromArgb(235, colour));
        using (GraphicsPath bar = RoundedRectPath(left * SS, top * SS, d * SS, barH * SS, d * SS / 2f))
            g.FillPath(brush, bar);
        g.FillEllipse(brush, left * SS, (top + barH + gap) * SS, d * SS, d * SS);
    }

    /// <summary>The padlock (see PadlockLayout): a rounded body with a keyhole and an arched shackle, antialiased, edges on the pixel grid.</summary>
    static void DrawPadlockHi(Graphics g, int px, Color colour)
    {
        PadlockRects r = PadlockLayout(px);
        using var brush = new SolidBrush(Color.FromArgb(235, colour));

        float bodyRadius = (px >= 24 ? 1.2f : 0.8f) * SS;
        using (GraphicsPath body = RoundedRectPath(r.Body.X * SS, r.Body.Y * SS, r.Body.Width * SS, r.Body.Height * SS, bodyRadius))
            g.FillPath(brush, body);

        using (GraphicsPath shackle = ShacklePath(r, px))
            g.FillPath(brush, shackle);

        if (r.Keyhole is { } hole)
        {
            // Punch the keyhole through the body so it stays legible on any taskbar colour.
            g.CompositingMode = CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.Transparent);
            g.FillRectangle(clear, hole.X * SS, hole.Y * SS, hole.Width * SS, hole.Height * SS);
            g.CompositingMode = CompositingMode.SourceOver;
        }
    }

    /// <summary>The shackle as one outline: two legs and a rounded top, `stroke` px thick, open at the bottom (it disappears into the body).</summary>
    static GraphicsPath ShacklePath(PadlockRects r, int px)
    {
        Rectangle topBar = r.Shackle[0];
        float stroke = topBar.Height * (float)SS;
        float x0 = topBar.X * (float)SS, x1 = (topBar.X + topBar.Width) * (float)SS;
        float y0 = topBar.Y * (float)SS, y1 = r.Body.Y * (float)SS + stroke;     // runs into the body, so no seam shows
        float w = x1 - x0;
        float R = MathF.Min(w / 2f, MathF.Max(1.5f * SS, stroke * 1.5f));
        float ri = MathF.Max(0f, R - stroke);

        var p = new GraphicsPath(FillMode.Winding);
        p.StartFigure();
        p.AddLine(x0, y1, x0, y0 + R);
        p.AddArc(x0, y0, 2 * R, 2 * R, 180, 90);
        p.AddLine(x0 + R, y0, x1 - R, y0);
        p.AddArc(x1 - 2 * R, y0, 2 * R, 2 * R, 270, 90);
        p.AddLine(x1, y0 + R, x1, y1);
        p.AddLine(x1, y1, x1 - stroke, y1);
        if (ri > 0.01f)
        {
            p.AddLine(x1 - stroke, y1, x1 - stroke, y0 + stroke + ri);
            p.AddArc(x1 - stroke - 2 * ri, y0 + stroke, 2 * ri, 2 * ri, 0, -90);
            p.AddLine(x1 - stroke - ri, y0 + stroke, x0 + stroke + ri, y0 + stroke);
            p.AddArc(x0 + stroke, y0 + stroke, 2 * ri, 2 * ri, 270, -90);
            p.AddLine(x0 + stroke, y0 + stroke + ri, x0 + stroke, y1);
        }
        else
        {
            p.AddLine(x1 - stroke, y1, x1 - stroke, y0 + stroke);
            p.AddLine(x1 - stroke, y0 + stroke, x0 + stroke, y0 + stroke);
            p.AddLine(x0 + stroke, y0 + stroke, x0 + stroke, y1);
        }
        p.CloseFigure();
        return p;
    }

    static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float radius)
    {
        float r = MathF.Min(radius, MathF.Min(w, h) / 2f);
        var p = new GraphicsPath();
        if (r <= 0.01f) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The padlock's pixel rectangles for a tray size (internal: the tests measure inside them).</summary>
    internal readonly record struct PadlockRects(Rectangle Body, Rectangle[] Shackle, Rectangle? Keyhole);

    internal static PadlockRects PadlockLayout(int px)
    {
        int stroke = Math.Max(1, (int)MathF.Round(px * 0.075f));
        int bodyW = Math.Max(6, (int)MathF.Round(px * 0.38f));
        if ((bodyW - px) % 2 != 0) bodyW++;                            // same parity as px, so the lock is exactly centred
        int bodyH = Math.Max(4, (int)MathF.Round(px * 0.25f));
        int shackleH = Math.Max(3, (int)MathF.Round(px * 0.19f));
        int shackleW = Math.Max(2 * stroke + 2, bodyW - 2 * Math.Max(1, (int)MathF.Round(px * 0.06f)));
        if ((shackleW - px) % 2 != 0) shackleW++;

        int total = shackleH + bodyH;
        int top = (int)MathF.Round((px - total) / 2f);
        int left = (px - bodyW) / 2;
        int shackleLeft = (px - shackleW) / 2;

        var body = new Rectangle(left, top + shackleH, bodyW, bodyH);
        var shackle = new[]
        {
            new Rectangle(shackleLeft, top, shackleW, stroke),                              // top bar
            new Rectangle(shackleLeft, top, stroke, shackleH),                              // left leg
            new Rectangle(shackleLeft + shackleW - stroke, top, stroke, shackleH),          // right leg
        };

        Rectangle? keyhole = null;
        if (px >= 20)
        {
            int hole = px >= 28 ? 2 : 1;
            keyhole = new Rectangle((px - hole) / 2, body.Y + (bodyH - hole) / 2, hole, hole * (bodyH >= 7 ? 2 : 1));
        }
        return new PadlockRects(body, shackle, keyhole);
    }

    /// <summary>
    /// The loading glyph (docs/multi-account.md "Loading"): the same grey ring as the Unknown icon but
    /// WITHOUT the "!" -- "not answered yet" is not "something is wrong" -- plus a brighter arc whose
    /// position is `frame` of `frameCount` steps round the ring. Goes through the same supersample +
    /// downsample pipeline as the other ring glyphs; LoadingFrames renders each frame once and caches it.
    /// </summary>
    public static Bitmap RenderLoading(int px, int frame, int frameCount, bool taskbarDark = true)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var (band, _, ringRect) = RingGeometry(s);

            Color grey = Palette.ForTaskbar(Palette.Dead, taskbarDark);
            using (var track = new Pen(Color.FromArgb(170, grey), band * 0.45f))
                g.DrawEllipse(track, ringRect);

            float start = -90f + 360f * frame / frameCount;
            using var arc = new Pen(Color.FromArgb(255, grey), band * 0.95f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ringRect, start, 100f);
        }
        return Downsample(hi, px, exhausted: false, dimAlpha: 1f);
    }

    /// <summary>
    /// Freshness.Stale overlay: 70 % opacity plus a small dot at 4 o'clock, per
    /// docs/forecast-and-states.md "Freshness ladder". The fade multiplies the alpha exactly once and the
    /// dot is antialiased (supersampled), so neither makes a stair-step. Consumes and disposes src.
    /// </summary>
    public static Bitmap ApplyStaleOverlay(Bitmap src, int px, bool taskbarDark = true)
    {
        try
        {
            Bitmap faded = ScaleAlpha(src, 0.70f);
            try
            {
                // 4 o'clock: 120 degrees clockwise from 12.
                float cx = px * 0.5f, cy = px * 0.5f;
                float r = px * 0.40f;
                double angle = 120.0 * Math.PI / 180.0;
                float dx = cx + r * (float)Math.Sin(angle);
                float dy = cy - r * (float)Math.Cos(angle);
                float dotR = Math.Max(1.3f, px * 0.10f);

                int s = px * SS;
                using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(hi))
                {
                    ConfigureHiRes(g);
                    using var dotBrush = new SolidBrush(taskbarDark ? Color.FromArgb(235, 0xF2, 0xF2, 0xF2) : Color.FromArgb(235, 0x2A, 0x2A, 0x2A));
                    g.FillEllipse(dotBrush, (dx - dotR) * SS, (dy - dotR) * SS, dotR * 2 * SS, dotR * 2 * SS);
                }
                using Bitmap dot = Downsample(hi, px, exhausted: false, dimAlpha: 1f);

                using var g2 = Graphics.FromImage(faded);
                g2.CompositingMode = CompositingMode.SourceOver;
                g2.InterpolationMode = InterpolationMode.NearestNeighbor; // same size: a pixel-exact composite
                g2.PixelOffsetMode = PixelOffsetMode.Half;
                g2.DrawImage(dot, new Rectangle(0, 0, px, px), 0, 0, px, px, GraphicsUnit.Pixel);
                return faded;
            }
            catch
            {
                faded.Dispose();
                throw;
            }
        }
        finally
        {
            src.Dispose();
        }
    }

    /// <summary>A copy of `src` with every pixel's (straight) alpha multiplied by `factor`; colours untouched.</summary>
    static Bitmap ScaleAlpha(Bitmap src, float factor)
    {
        int w = src.Width, h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes = new byte[data.Stride * h];
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { src.UnlockBits(data); }

        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = (byte)Math.Clamp((int)MathF.Round(bytes[i] * factor), 0, 255);

        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            var od = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, od.Scan0, bytes.Length); }
            finally { outBmp.UnlockBits(od); }
            return outBmp;
        }
        catch
        {
            outBmp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The single entry point everything live/demo renders the tray icon through
    /// (docs/forecast-and-states.md, "Icon"). Pure function of QuotaIconParams, so
    /// the caller (IconSlot) can use the same params both to render and to detect
    /// "nothing changed" without duplicating the QuotaView interpretation.
    /// </summary>
    public static Bitmap RenderQuota(QuotaIconParams p)
    {
        if (p.Loading) return RenderLoading(p.Px, p.LoadingFrame, LoadingFrames.FrameCount, p.TaskbarDark);
        if (p.Outline) return RenderOutline(p.Px, p.Padlock, p.TaskbarDark);

        // 0.85 on a dark taskbar, per docs/forecast-and-states.md "Exhausted": with the real
        // critical red (not a lightened tint) and a >= 3:1 target (not >= 5:1 -- this state
        // must read as quieter than an active one, not merely legible), 0.85 clears 3:1 with a
        // real margin (measured ~3.1:1; see IconContrastTests) while dimming it well below the
        // undimmed Tight/Safe glyphs.
        float dimAlpha = ExhaustedDimAlpha;
        Bitmap bmp = p.Exhausted
            ? RenderExhaustedGlyph(p.Px, p.PieFrac, Color.FromArgb(p.RingArgb), dimAlpha, p.TaskbarDark)
            : Render(p.Px, p.RingFrac, p.PieFrac, p.ForecastFrac, p.SessionForecastFrac, Color.FromArgb(p.RingArgb), Color.FromArgb(p.PieArgb), exhausted: false, p.TaskbarDark, dimAlpha);

        return p.Stale ? ApplyStaleOverlay(bmp, p.Px, p.TaskbarDark) : bmp;
    }

    /// <summary>
    /// The exhausted glyph (solid ring + countdown pie, docs/forecast-and-states.md "Exhausted"): a FULL,
    /// continuous ring in the critical colour (dimmed), not the earlier dashed outline -- the dashes read as
    /// a lifebuoy rather than "blocked". Only the inner countdown pie moves.
    ///
    /// Antialiased like every other glyph: drawn at SS times the size and box-filtered down once, with the
    /// dim applied exactly once, as a plain multiplication of the final alpha (Downsample). The stroke is
    /// wide enough that its core reaches full coverage -- so the core's alpha is exactly the dim level and
    /// the contrast tests measure that core (IconContrastTests), never the antialiased fringe. (An earlier
    /// version turned antialiasing OFF to get there, which left the stair-stepped ring users saw.)
    /// </summary>
    static Bitmap RenderExhaustedGlyph(int px, double pieFrac, Color color, float dimAlpha, bool taskbarDark)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            ConfigureHiRes(g);
            var (band, rOuter, ringRect) = RingGeometry(s);

            using (var trackPen = new Pen(TrackColor(40, taskbarDark), band))
                g.DrawArc(trackPen, ringRect, 0, 360);

            using (var ring = new Pen(color, Math.Max(1.6f * SS, band * 0.95f)))
                g.DrawEllipse(ring, ringRect);

            float gap = band * 0.42f;
            float rPieOuter = rOuter - band * 0.5f - gap;
            using (var erase = new SolidBrush(Color.Transparent))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                float rk = rPieOuter + gap;
                g.FillEllipse(erase, s * 0.5f - rk, s * 0.5f - rk, rk * 2, rk * 2);
                g.CompositingMode = CompositingMode.SourceOver;
            }

            if (pieFrac > 0)
            {
                using var pb = new SolidBrush(color);
                var pr = new RectangleF(s * 0.5f - rPieOuter, s * 0.5f - rPieOuter, rPieOuter * 2, rPieOuter * 2);
                g.FillPie(pb, pr, -90f, (float)(Math.Min(1.0, pieFrac) * 360.0));
            }
        }
        return Downsample(hi, px, exhausted: true, dimAlpha);
    }

    /// <summary>
    /// Box-filters a supersampled glyph down to px x px: every output pixel is the exact area average of
    /// its SS x SS block, computed on premultiplied colour so a transparent neighbour never tints an edge
    /// (the usual dark/light halo). The result is straight (non-premultiplied) ARGB, which is what the
    /// ICO writer and GDI+ expect. `dimAlpha` (the exhausted glyph's quiet-down) multiplies the final
    /// alpha exactly once; colours are untouched. A stroke whose core is fully covered ends at alpha
    /// 255 x dim, exactly.
    /// </summary>
    static Bitmap Downsample(Bitmap hi, int px, bool exhausted, float dimAlpha)
    {
        int s = hi.Width;
        int f = s / px;
        var data = hi.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] src = new byte[data.Stride * s];
        int stride = data.Stride;
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, src, 0, src.Length); }
        finally { hi.UnlockBits(data); }

        float dim = exhausted ? dimAlpha : 1f;
        byte[] dst = new byte[px * px * 4];
        double area = (double)f * f;
        for (int oy = 0; oy < px; oy++)
        {
            for (int ox = 0; ox < px; ox++)
            {
                double sa = 0, sr = 0, sg = 0, sb = 0;
                for (int yy = oy * f; yy < (oy + 1) * f; yy++)
                {
                    int row = yy * stride;
                    for (int xx = ox * f; xx < (ox + 1) * f; xx++)
                    {
                        int i = row + xx * 4;
                        double a = src[i + 3];
                        sa += a;
                        sb += src[i] * a;
                        sg += src[i + 1] * a;
                        sr += src[i + 2] * a;
                    }
                }

                int o = (oy * px + ox) * 4;
                if (sa <= 0) continue;
                dst[o] = (byte)Math.Clamp((int)Math.Round(sb / sa), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)Math.Round(sg / sa), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)Math.Round(sr / sa), 0, 255);
                dst[o + 3] = (byte)Math.Clamp((int)Math.Round(sa / area * dim), 0, 255);
            }
        }

        var outBmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        try
        {
            var od = outBmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(dst, 0, od.Scan0, dst.Length); }
            finally { outBmp.UnlockBits(od); }
            return outBmp;
        }
        catch
        {
            outBmp.Dispose();
            throw;
        }
    }

    /// <summary>Ring stroke width and bounding rect, shared by Render() and RenderRing() so they never disagree.</summary>
    static (float band, float rOuter, RectangleF ringRect) RingGeometry(int s)
    {
        float band = s * 0.155f; // ring stroke width
        float rOuter = s * 0.5f - band * 0.5f - s * 0.02f;
        var ringRect = new RectangleF(s * 0.5f - rOuter, s * 0.5f - rOuter, rOuter * 2, rOuter * 2);
        return (band, rOuter, ringRect);
    }
}

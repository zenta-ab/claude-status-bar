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
    const int SS = 8; // supersample factor

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

            using (var trackPen = new Pen(Color.FromArgb(exhausted ? 40 : 70, 255, 255, 255), band))
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

    /// <summary>Grey outline, no fills, no arcs -- Freshness.Unknown: never show a number or a verdict colour.</summary>
    public static Bitmap RenderOutline(int px)
    {
        int s = px * SS;
        using var hi = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var (band, _, ringRect) = RingGeometry(s);
            using var pen = new Pen(Color.FromArgb(170, Palette.Dead), band * 0.45f);
            g.DrawEllipse(pen, ringRect);
        }
        return Downsample(hi, px, exhausted: false, dimAlpha: 1f);
    }

    /// <summary>
    /// Freshness.Stale overlay: 70% global opacity plus a small dot at 4 o'clock,
    /// per docs/forecast-and-states.md "Freshness ladder". Consumes and disposes src.
    /// </summary>
    public static Bitmap ApplyStaleOverlay(Bitmap src, int px)
    {
        // src is consumed and disposed on every path (Codex review Medium #16); outBmp is
        // disposed too if anything throws before ownership transfers to the caller via return.
        try
        {
            var outBmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(outBmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    var cm = new ColorMatrix { Matrix33 = 0.70f };
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(src, new Rectangle(0, 0, px, px), 0, 0, px, px, GraphicsUnit.Pixel, ia);
                    }

                    // 4 o'clock: 120 degrees clockwise from 12.
                    float cx = px * 0.5f, cy = px * 0.5f;
                    float r = px * 0.40f;
                    double angle = 120.0 * Math.PI / 180.0;
                    float dx = cx + r * (float)Math.Sin(angle);
                    float dy = cy - r * (float)Math.Cos(angle);
                    float dotR = Math.Max(1.3f, px * 0.10f);
                    using var dotBrush = new SolidBrush(Color.FromArgb(235, 0xF2, 0xF2, 0xF2));
                    g.FillEllipse(dotBrush, dx - dotR, dy - dotR, dotR * 2, dotR * 2);
                }
                return outBmp;
            }
            catch
            {
                outBmp.Dispose();
                throw;
            }
        }
        finally
        {
            src.Dispose();
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
        if (p.Outline) return RenderOutline(p.Px);

        // The doc names 0.85 as the dark-taskbar target, but measured (IconContrastTests):
        // #9AA3AE at exactly 0.85 alpha over #202020 tops out at ~4.98:1 -- a hair under the
        // required 5:1, and that is the *ceiling* (a fully opaque, non-antialiased pixel);
        // any real edge softening only pulls it lower. 0.90 clears 5:1 with real margin
        // (measured ~5.3:1) while still reading as "dimmed" against a dark taskbar.
        float dimAlpha = p.TaskbarDark ? 0.90f : 0.42f;
        Bitmap bmp = p.Exhausted
            ? RenderExhaustedGlyph(p.Px, p.PieFrac, Color.FromArgb(p.RingArgb), dimAlpha)
            : Render(p.Px, p.RingFrac, p.PieFrac, p.ForecastFrac, p.SessionForecastFrac, Color.FromArgb(p.RingArgb), Color.FromArgb(p.PieArgb), exhausted: false, p.TaskbarDark, dimAlpha);

        return p.Stale ? ApplyStaleOverlay(bmp, p.Px) : bmp;
    }

    /// <summary>
    /// The exhausted glyph (dashed ring + countdown pie, docs/forecast-and-states.md
    /// "Exhausted"), drawn directly at output resolution rather than through the
    /// supersample+downsample pipeline the other renders use. Measured reason: at
    /// 16px the dashed ring's stroke is only ~2px wide, and downsampling a
    /// supersampled version of it (bicubic OR bilinear) never lets its interior
    /// reach true alpha 255 -- every "solid" pixel stayed partially blended with
    /// the transparent supersampled edges, capping contrast on #202020 around
    /// 4.5:1, short of the required 5:1 (see IconContrastTests). Drawing directly
    /// at 16px with GDI+'s own edge-only antialiasing leaves the stroke's actual
    /// interior at full alpha, which the dimAlpha ColorMatrix then scales exactly
    /// once -- no compounding, no interpolation loss.
    /// </summary>
    static Bitmap RenderExhaustedGlyph(int px, double pieFrac, Color color, float dimAlpha)
    {
        var canvas = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        try
        {
            using (var g = Graphics.FromImage(canvas))
            {
                // No antialiasing here: dimAlpha (0.85 on a dark taskbar) already caps every
                // pixel's deliverable alpha well below 255, leaving almost no headroom for AA's
                // partial pixel coverage on top of that without dropping under the required 5:1
                // contrast (measured: even a single AA-softened ring pixel was enough to fall
                // to ~4.5-4.8:1). A crisp edge is an acceptable look for a dashed "blocked"
                // indicator, and it is what lets every interior pixel hit the true ceiling.
                g.SmoothingMode = SmoothingMode.None;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                var (band, rOuter, ringRect) = RingGeometry(px);

                using (var trackPen = new Pen(Color.FromArgb(40, 255, 255, 255), band))
                    g.DrawArc(trackPen, ringRect, 0, 360);

                using (var dash = new Pen(color, Math.Max(1.6f, band * 0.95f)) { DashStyle = DashStyle.Custom, DashPattern = new[] { 2.2f, 2.0f } })
                    g.DrawArc(dash, ringRect, 0, 360);

                float gap = band * 0.42f;
                float rPieOuter = rOuter - band * 0.5f - gap;
                using (var erase = new SolidBrush(Color.Transparent))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    float rk = rPieOuter + gap;
                    g.FillEllipse(erase, px * 0.5f - rk, px * 0.5f - rk, rk * 2, rk * 2);
                    g.CompositingMode = CompositingMode.SourceOver;
                }

                if (pieFrac > 0)
                {
                    using var pb = new SolidBrush(color);
                    var pr = new RectangleF(px * 0.5f - rPieOuter, px * 0.5f - rPieOuter, rPieOuter * 2, rPieOuter * 2);
                    g.FillPie(pb, pr, -90f, (float)(Math.Min(1.0, pieFrac) * 360.0));
                }
            }

            var outBmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            try
            {
                using (var g2 = Graphics.FromImage(outBmp))
                {
                    var cm = new ColorMatrix { Matrix33 = dimAlpha };
                    using var ia = new ImageAttributes();
                    ia.SetColorMatrix(cm);
                    g2.DrawImage(canvas, new Rectangle(0, 0, px, px), 0, 0, px, px, GraphicsUnit.Pixel, ia);
                }
                return outBmp;
            }
            catch
            {
                outBmp.Dispose();
                throw;
            }
        }
        finally
        {
            // canvas is a newly-owned intermediate bitmap (Codex review Medium #16): must be
            // disposed on every path, not only the success path the original code covered.
            canvas.Dispose();
        }
    }

    static Bitmap Downsample(Bitmap hi, int px, bool exhausted, float dimAlpha)
    {
        // outBmp is the value this hands off to its caller; disposed here only if something
        // throws before that handoff completes (Codex review Medium #16).
        var outBmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        try
        {
            using var g2 = Graphics.FromImage(outBmp);
            // Bilinear, not bicubic: bicubic's negative-lobe overshoot/undershoot keeps even a
            // fully-opaque supersampled stroke from downsampling to alpha 255 unless it is many
            // output pixels wide -- measured to cap the exhausted ring's alpha around 200/255,
            // which was not enough headroom to clear 5:1 contrast on #202020 at 16px (see
            // IconContrastTests). Bilinear is a plain weighted average, so a stroke a couple of
            // output pixels wide still downsamples its interior to true full alpha.
            g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g2.CompositingQuality = CompositingQuality.HighQuality;
            g2.Clear(Color.Transparent);
            if (exhausted)
            {
                var cm = new ColorMatrix { Matrix33 = dimAlpha };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g2.DrawImage(hi, new Rectangle(0, 0, px, px), 0, 0, hi.Width, hi.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                g2.DrawImage(hi, new Rectangle(0, 0, px, px));
            }
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

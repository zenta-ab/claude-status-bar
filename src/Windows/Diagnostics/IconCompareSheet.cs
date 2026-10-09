using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Diagnostics;

/// <summary>
/// `--capture-icon-compare &lt;dir&gt;`: writes one sheet per tray size (16/20/24/32 px) showing EVERY icon
/// state exactly the way Windows will show it -- rendered by GaugeRenderer, packed into an in-memory
/// ICO, loaded with `new Icon(stream, size)` and drawn with Graphics.DrawIcon onto the dark and the
/// light taskbar colour -- each enlarged 6x with nearest-neighbour so every pixel edge is visible.
/// Used to LOOK for stair-stepped edges and jagged diagonals; never part of the running app.
/// </summary>
public static class IconCompareSheet
{
    const int Scale = 6;
    const int PerRow = 7;
    static readonly Color Dark = Color.FromArgb(255, 0x20, 0x20, 0x20);
    static readonly Color Light = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var states = new List<(string Name, QuotaView View, DateTimeOffset At)>();
        foreach (DemoQuotaSource.DemoState s in DemoQuotaSource.Build(now))
        {
            if (s.Key == "loading")
            {
                for (int f = 0; f < 8; f += 3) states.Add(($"loading f{f}", s.View, now.AddMilliseconds(f * LoadingFrames.FrameIntervalMs - (now.ToUnixTimeMilliseconds() / LoadingFrames.FrameIntervalMs % LoadingFrames.FrameCount) * LoadingFrames.FrameIntervalMs)));
            }
            else
            {
                states.Add((s.Key, s.View, now));
            }
        }

        foreach (int px in new[] { 16, 20, 24, 32 })
        {
            int cellW = px * Scale + 10;
            int cellH = 2 * px * Scale + 30;
            int rows = (states.Count + PerRow - 1) / PerRow;
            using var sheet = new Bitmap(cellW * PerRow, cellH * rows, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(255, 0x60, 0x60, 0x60));
                using var font = new Font("Segoe UI", 9f);
                for (int i = 0; i < states.Count; i++)
                {
                    int cx = i % PerRow * cellW + 5, cy = i / PerRow * cellH + 4;
                    QuotaIconParams darkP = QuotaIconParams.Build(states[i].View, px, taskbarDark: true, states[i].At);
                    QuotaIconParams lightP = QuotaIconParams.Build(states[i].View, px, taskbarDark: false, states[i].At);
                    using Bitmap onDark = ThroughIconPath(darkP, Dark);
                    using Bitmap onLight = ThroughIconPath(lightP, Light);

                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(onDark, new Rectangle(cx, cy, px * Scale, px * Scale), 0, 0, px, px, GraphicsUnit.Pixel);
                    g.DrawImage(onLight, new Rectangle(cx, cy + px * Scale, px * Scale, px * Scale), 0, 0, px, px, GraphicsUnit.Pixel);
                    g.DrawString(states[i].Name, font, Brushes.White, cx, cy + 2 * px * Scale + 2);
                }
            }
            sheet.Save(Path.Combine(dir, $"sheet-{px}px.png"), ImageFormat.Png);
        }
        Console.WriteLine($"icon compare sheets written to {dir}");
        Console.WriteLine($"tray icon size on this machine (IconSlot.QueryTrayIconPixelSize): {IconSlot.QueryTrayIconPixelSize()} px");
        return 0;
    }

    /// <summary>The exact route a tray icon takes: ICO bytes -> new Icon(stream, size) -> DrawIcon, onto the taskbar colour.</summary>
    static Bitmap ThroughIconPath(QuotaIconParams p, Color background)
    {
        using Bitmap glyph = p.Loading
            ? GaugeRenderer.RenderLoading(p.Px, p.LoadingFrame, LoadingFrames.FrameCount, p.TaskbarDark)
            : GaugeRenderer.RenderQuota(p);
        byte[] ico = IconFactory.BuildIco(glyph);
        using var ms = new MemoryStream(ico);
        using var icon = new Icon(ms, new Size(p.Px, p.Px));

        var bmp = new Bitmap(p.Px, p.Px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(background);
        g.DrawIcon(icon, new Rectangle(0, 0, p.Px, p.Px));
        return bmp;
    }
}

using System.Drawing;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Icons/IconFactory.cs: what Windows is handed. Frames must be 32-bit with REAL alpha, and the 1-bit
/// AND mask must be all zeros -- otherwise it would cut a hard, stair-stepped edge no matter how well the
/// glyph was antialiased. Also pins the whole route (ICO bytes -> new Icon(stream, size) -> pixels).
/// </summary>
public class IconFactoryTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static Bitmap Glyph(string state, int px) =>
        GaugeRenderer.RenderQuota(QuotaIconParams.Build(DemoQuotaSource.Build(Now).First(s => s.Key == state).View, px, taskbarDark: true, Now));

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Ico_IsThirtyTwoBitWithAnAllZeroMask_AndKeepsEveryAlphaValue(int px)
    {
        using Bitmap glyph = Glyph("spent_session", px);
        byte[] ico = IconFactory.BuildIco(glyph);

        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));          // reserved
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));          // type: icon
        Assert.Equal(1, BitConverter.ToUInt16(ico, 4));          // one image
        Assert.Equal(32, BitConverter.ToUInt16(ico, 6 + 6));     // directory entry: bit count
        int size = BitConverter.ToInt32(ico, 6 + 8), offset = BitConverter.ToInt32(ico, 6 + 12);
        Assert.Equal(40, BitConverter.ToInt32(ico, offset));                 // BITMAPINFOHEADER
        Assert.Equal(px, BitConverter.ToInt32(ico, offset + 4));
        Assert.Equal(px * 2, BitConverter.ToInt32(ico, offset + 8));         // XOR + AND heights
        Assert.Equal(32, BitConverter.ToUInt16(ico, offset + 14));

        int xorBytes = px * px * 4;
        int maskStride = ((px + 31) / 32) * 4;
        byte[] mask = ico.AsSpan(offset + 40 + xorBytes, maskStride * px).ToArray();
        Assert.Equal(offset + 40 + xorBytes + mask.Length, offset + size);
        Assert.All(mask, b => Assert.Equal(0, b)); // transparency comes from alpha alone: no 1-bit edge

        // The XOR plane holds the glyph's own pixels, bottom-up, alpha included.
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                int i = offset + 40 + ((px - 1 - y) * px + x) * 4;
                Color c = glyph.GetPixel(x, y);
                Assert.Equal(c.A, ico[i + 3]);
                if (c.A > 0) { Assert.Equal(c.B, ico[i]); Assert.Equal(c.G, ico[i + 1]); Assert.Equal(c.R, ico[i + 2]); }
            }
    }

    [Theory]
    [InlineData("spent_session", 16)]
    [InlineData("spent_weekly", 20)]
    [InlineData("safe", 24)]
    [InlineData("not_logged_in", 16)]
    [InlineData("unknown", 32)]
    public void IconPath_NewIconFromTheIco_KeepsTheGradedAlpha(string state, int px)
    {
        using Bitmap glyph = Glyph(state, px);
        byte[] ico = IconFactory.BuildIco(glyph);
        using var ms = new MemoryStream(ico);
        using var icon = new Icon(ms, new Size(px, px));
        using Bitmap viaIcon = icon.ToBitmap();

        Assert.Equal(px, viaIcon.Width);
        int graded = 0, differing = 0;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                int a = glyph.GetPixel(x, y).A, b = viaIcon.GetPixel(x, y).A;
                if (a > 0 && a < 255) graded++;
                if (Math.Abs(a - b) > 1) differing++;
            }
        Assert.True(graded > px, "the glyph has no graded alpha to carry");
        Assert.Equal(0, differing); // Windows' own loader hands back the same alpha: nothing was cut to 1 bit
    }
}

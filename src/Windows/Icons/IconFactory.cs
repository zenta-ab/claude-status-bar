using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// Hand-builds a multi-frame .ICO byte stream from ARGB bitmaps and hands the
/// result to `new Icon(stream, size)`. This is the whole point: an Icon built
/// this way OWNS its handle, so Dispose() actually calls DestroyIcon. The
/// alternative -- Bitmap.GetHicon() + Icon.FromHandle() -- sets ownHandle=false,
/// makes Dispose() a no-op, and leaks GDI/USER handles (measured: ~2 GDI + 1 USER
/// per call, dead in ~1.7 days at one update per 30s). Ported near-verbatim from
/// scratchpad/icontest/Program.cs, which measured a 0/0 handle delta over 2000
/// iterations for this exact path.
/// </summary>
public static class IconFactory
{
    public static byte[] BuildIco(params Bitmap[] frames)
    {
        var images = new List<byte[]>();
        foreach (var bmp in frames) images.Add(EncodeBmpFrame(bmp));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Length);
        int offset = 6 + 16 * frames.Length;
        for (int i = 0; i < frames.Length; i++)
        {
            int side = frames[i].Width;
            w.Write((byte)(side >= 256 ? 0 : side));
            w.Write((byte)(side >= 256 ? 0 : side));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        w.Flush();
        return ms.ToArray();
    }

    static byte[] EncodeBmpFrame(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        // Allocated BEFORE LockBits (Codex review Medium #16): if this allocation itself
        // fails (OOM), LockBits/UnlockBits is never unbalanced -- the old order locked bmp
        // first and allocated second, so an allocation failure there skipped UnlockBits.
        var xor = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr src = data.Scan0 + (h - 1 - y) * data.Stride; // ICO rows are bottom-up
                Marshal.Copy(src, xor, y * w * 4, w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }

        int maskStride = ((w + 31) / 32) * 4;
        var and = new byte[maskStride * h]; // all zero: alpha channel governs

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(xor.Length + and.Length);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(xor); bw.Write(and);
        bw.Flush();
        return ms.ToArray();
    }
}

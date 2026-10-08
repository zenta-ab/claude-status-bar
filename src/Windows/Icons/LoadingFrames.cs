using System.Collections.Concurrent;
using System.Drawing;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// The loading icon (docs/multi-account.md "Loading"): a grey ring, WITHOUT the "!" of an unreadable
/// account, with a short brighter arc travelling round it. A small fixed set of frames is rendered
/// ONCE per tray size and kept as ready-made .ICO bytes -- the same in-memory ICO route every other
/// icon takes (IconFactory), never Bitmap.GetHicon + Icon.FromHandle -- so animating costs one
/// `new Icon(stream)` per frame (retired by IconSlot's usual two-generation scheme) and no drawing.
/// At most <see cref="FramesPerSecond"/> frames a second, and only while some account is loading
/// (StatusBarApplicationContext's loading timer runs only then).
/// </summary>
public static class LoadingFrames
{
    public const int FrameCount = 8;
    public const int FramesPerSecond = 8;
    public const int FrameIntervalMs = 1000 / FramesPerSecond;

    static readonly ConcurrentDictionary<(int Px, int Frame), byte[]> Cache = new();

    /// <summary>Which of the frames shows at this instant: the same for every icon, so several loading accounts spin in step.</summary>
    public static int FrameAt(DateTimeOffset now) =>
        (int)(now.ToUnixTimeMilliseconds() / FrameIntervalMs % FrameCount);

    /// <summary>The frame's ICO bytes for a tray size; rendered the first time it is asked for, then cached.</summary>
    public static byte[] GetIcoBytes(int px, int frame) =>
        Cache.GetOrAdd((px, ((frame % FrameCount) + FrameCount) % FrameCount), key =>
        {
            using Bitmap bmp = GaugeRenderer.RenderLoading(key.Px, key.Frame, FrameCount);
            return IconFactory.BuildIco(bmp);
        });

    /// <summary>How many frames are cached (tests: the set is bounded however long the icon spins).</summary>
    public static int CachedFrames => Cache.Count;
}

using System.Drawing;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// The pixel font of the exhausted icon's countdown (docs/forecast-and-states.md "Exhausted"): 0-9, d, h
/// and m, purpose-drawn on a 16 px grid instead of scaled from a system font, because at 16 px a scaled
/// font smears into grey. Every stroke is whole pixels on the grid, so a straight stroke is crisp; the
/// round glyphs get their roundness from cut corner pixels, not from antialiasing.
///
/// Two sets, because the width of the icon is fixed and the number of characters is not:
///   * A, for the two-character tokens ("3d", "9h", "1m"): 9 px tall, digits 6 px wide with 2 px stems;
///   * B, for the three-character tokens ("23h", "45m", "10m"): 7 px tall, digits 4 px wide, a real 5 px "m",
///     which with 1 px gaps is 14-15 px -- the whole width of a 16 px icon.
/// The text is centred on the WHOLE icon and lies over the ring (a dim backdrop, see
/// GaugeRenderer.ExhaustedDimAlpha; an outline cut out of the ring round the digits was tried and dropped:
/// against a ring this faint it only broke the ring up). Larger icons scale the same design by a
/// WHOLE factor (px/16, rounded down: 1x up to 31 px, 2x from 32 px), so every stroke stays an even number of
/// pixels -- a fractional factor gave uneven 1-2 px strokes at 20 and 24 px, which looked worse than a
/// slightly smaller, crisp text.
/// </summary>
internal static class CountdownGlyphs
{
    /// <summary>The pixel rectangles of a token's ink on a px x px icon, and their bounding box.</summary>
    internal readonly record struct Layout(IReadOnlyList<Rectangle> Ink, Rectangle Bounds);

    // '#' is ink; a glyph may have fewer rows than its set (the "m"), then it sits on the baseline.
    static readonly Dictionary<char, string[]> SetA = new()
    {
        ['0'] = new[] { ".####.", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", ".####." },
        ['1'] = new[] { "..##", ".###", "####", "..##", "..##", "..##", "..##", "..##", "..##" },
        ['2'] = new[] { ".####.", "##..##", "....##", "....##", "...##.", "..##..", ".##...", "##....", "######" },
        ['3'] = new[] { ".####.", "##..##", "....##", "..###.", "..###.", "....##", "....##", "##..##", ".####." },
        ['4'] = new[] { "....##", "...###", "..####", ".##.##", "##..##", "######", "....##", "....##", "....##" },
        ['5'] = new[] { "######", "##....", "##....", "#####.", "##..##", "....##", "....##", "##..##", ".####." },
        ['6'] = new[] { ".####.", "##..##", "##....", "##....", "#####.", "##..##", "##..##", "##..##", ".####." },
        ['7'] = new[] { "######", "....##", "....##", "...##.", "...##.", "..##..", "..##..", ".##...", ".##..." },
        ['8'] = new[] { ".####.", "##..##", "##..##", ".####.", ".####.", "##..##", "##..##", "##..##", ".####." },
        ['9'] = new[] { ".####.", "##..##", "##..##", "##..##", ".#####", "....##", "....##", "##..##", ".####." },
        ['d'] = new[] { "....##", "....##", "....##", ".#####", "##..##", "##..##", "##..##", "##..##", ".#####" },
        ['h'] = new[] { "##....", "##....", "##....", "#####.", "##..##", "##..##", "##..##", "##..##", "##..##" },
        ['m'] = new[] { ".#####.", "##.#.##", "##.#.##", "##.#.##", "##.#.##", "##.#.##" },
    };

    static readonly Dictionary<char, string[]> SetB = new()
    {
        ['0'] = new[] { ".##.", "#..#", "#..#", "#..#", "#..#", "#..#", ".##." },
        ['1'] = new[] { ".#.", "##.", ".#.", ".#.", ".#.", ".#.", ".#." },
        ['2'] = new[] { ".##.", "#..#", "...#", "..#.", ".#..", "#...", "####" },
        ['3'] = new[] { ".##.", "#..#", "...#", "..#.", "...#", "#..#", ".##." },
        ['4'] = new[] { "..#.", ".##.", "#.#.", "#..#", "####", "..#.", "..#." },
        ['5'] = new[] { "####", "#...", "###.", "...#", "...#", "#..#", ".##." },
        ['6'] = new[] { ".##.", "#...", "#...", "###.", "#..#", "#..#", ".##." },
        ['7'] = new[] { "####", "...#", "...#", "..#.", "..#.", ".#..", ".#.." },
        ['8'] = new[] { ".##.", "#..#", "#..#", ".##.", "#..#", "#..#", ".##." },
        ['9'] = new[] { ".##.", "#..#", "#..#", ".###", "...#", "...#", ".##." },
        ['d'] = new[] { "...#", "...#", ".###", "#..#", "#..#", "#..#", ".###" },
        ['h'] = new[] { "#...", "#...", "###.", "#..#", "#..#", "#..#", "#..#" },
        ['m'] = new[] { "##.#.", "#.#.#", "#.#.#", "#.#.#", "#.#.#" },
    };

    internal const int DesignSize = 16;

    internal static bool CanDraw(string text) => text.Length is >= 1 and <= 3 && text.All(SetA.ContainsKey);

    /// <summary>Which set a token uses: three characters need the narrow one.</summary>
    internal static IReadOnlyDictionary<char, string[]> SetFor(int length) => length >= 3 ? SetB : SetA;

    internal static int RowsOf(int length) => length >= 3 ? 7 : 9;

    /// <summary>
    /// The ink of `text`, centred on the icon (an odd leftover sits half a pixel left/up of true centre: a
    /// whole-pixel grid cannot do better) and scaled by a whole factor. Throws for anything the font does not have.
    /// </summary>
    internal static Layout For(string text, int px)
    {
        if (!CanDraw(text)) throw new ArgumentException($"no countdown glyphs for \"{text}\"", nameof(text));
        IReadOnlyDictionary<char, string[]> set = SetFor(text.Length);
        int rows = RowsOf(text.Length);
        const int gap = 1;

        int width = text.Sum(c => set[c][0].Length) + (text.Length - 1) * gap;
        int k = Math.Max(1, px / DesignSize);
        int left = (px - width * k) / 2, top = (px - rows * k) / 2;

        var ink = new List<Rectangle>();
        int x = 0;
        foreach (char ch in text)
        {
            string[] glyph = set[ch];
            int rowOffset = rows - glyph.Length;
            for (int r = 0; r < glyph.Length; r++)
                for (int c = 0; c < glyph[r].Length; c++)
                    if (glyph[r][c] == '#')
                    {
                        ink.Add(new Rectangle(left + (x + c) * k, top + (rowOffset + r) * k, k, k));
                    }
            x += glyph[0].Length + gap;
        }

        return new Layout(ink, new Rectangle(left, top, width * k, rows * k));
    }

    /// <summary>Which pixels of a px x px icon are ink.</summary>
    internal static bool[,] InkMask(Layout layout, int px)
    {
        var ink = new bool[px, px];
        foreach (Rectangle r in layout.Ink)
            for (int y = Math.Max(0, r.Top); y < Math.Min(px, r.Bottom); y++)
                for (int x = Math.Max(0, r.Left); x < Math.Min(px, r.Right); x++) ink[x, y] = true;
        return ink;
    }
}

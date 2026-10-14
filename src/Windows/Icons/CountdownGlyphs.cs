using System.Drawing;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// The pixel font of the exhausted icon's countdown (docs/forecast-and-states.md "Exhausted"): 0-9, d, h
/// and m, purpose-drawn on a 16 px grid instead of scaled from a system font, because at 16 px a scaled
/// font smears into grey. Every stroke is a whole pixel on the grid, so a straight stroke is crisp; the
/// round glyphs get their roundness from cut corner pixels, not from antialiasing.
///
/// ONE small set for every token: 7 px tall, digits 4 px wide with 1 px stems, "1" 3 px, "d"/"h" 4 px and a
/// real 5 px "m", with 1 px gaps -- 9-10 px for two characters (which sit inside the ring's clear interior at
/// 16 px) and 14-15 px for three (which run a little over the ring). The text is deliberately calm: a bigger
/// 9 px set with 2 px stems was shipped first and the user found it "almost too much" (top apps rarely write
/// the reset time into the glyph at all, and Microsoft's guidance prefers muted state over loud glyphs).
/// Larger icons scale the same design by a WHOLE factor (px/16, rounded down: 1x up to 31 px, 2x from 32 px),
/// so every stroke stays an even number of pixels.
/// </summary>
internal static class CountdownGlyphs
{
    /// <summary>The pixel rectangles of a token's ink on a px x px icon, and their bounding box.</summary>
    internal readonly record struct Layout(IReadOnlyList<Rectangle> Ink, Rectangle Bounds);

    // '#' is ink; a glyph may have fewer rows than Rows (the "m"), then it sits on the baseline.
    static readonly Dictionary<char, string[]> Shapes = new()
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
    internal const int Rows = 7;

    internal static bool CanDraw(string text) => text.Length is >= 1 and <= 3 && text.All(Shapes.ContainsKey);

    /// <summary>The shape of one character (rows of '#' and '.'), for tests.</summary>
    internal static IReadOnlyList<string> ShapeOf(char c) => Shapes[c];

    /// <summary>
    /// The ink of `text`, centred on the icon (an odd leftover sits half a pixel left/up of true centre: a
    /// whole-pixel grid cannot do better) and scaled by a whole factor. Throws for anything the font does not have.
    /// </summary>
    internal static Layout For(string text, int px)
    {
        if (!CanDraw(text)) throw new ArgumentException($"no countdown glyphs for \"{text}\"", nameof(text));
        const int gap = 1;

        int width = text.Sum(c => Shapes[c][0].Length) + (text.Length - 1) * gap;
        int k = Math.Max(1, px / DesignSize);
        int left = (px - width * k) / 2, top = (px - Rows * k) / 2;

        var ink = new List<Rectangle>();
        int x = 0;
        foreach (char ch in text)
        {
            string[] glyph = Shapes[ch];
            int rowOffset = Rows - glyph.Length;
            for (int r = 0; r < glyph.Length; r++)
                for (int c = 0; c < glyph[r].Length; c++)
                    if (glyph[r][c] == '#')
                        ink.Add(new Rectangle(left + (x + c) * k, top + (rowOffset + r) * k, k, k));
            x += glyph[0].Length + gap;
        }
        return new Layout(ink, new Rectangle(left, top, width * k, Rows * k));
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

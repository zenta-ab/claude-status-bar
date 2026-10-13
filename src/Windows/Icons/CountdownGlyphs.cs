using System.Drawing;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// The pixel font of the exhausted icon's countdown (docs/forecast-and-states.md "Exhausted"): 0-9, d, h
/// and m, drawn purpose-built on a pixel grid instead of scaled from a system font, because at 16 px a
/// scaled font smears into grey. Every stroke is a whole number of output pixels wide and sits exactly on
/// the grid, so a straight stroke is a crisp one; the glyphs have no curves, so nothing is antialiased.
///
/// The shapes are 3 pixels wide (the digit "1" 2) and 5 tall, so three characters plus their two gaps are
/// 11 pixels -- the most that fits inside the thin ring of a 16 px icon. Larger icons get the same shapes
/// taller (7 rows, 20/24 px) or with a thicker stroke (2 px cells, 32 px and up), see <see cref="StyleFor"/>.
/// </summary>
internal static class CountdownGlyphs
{
    /// <summary>The pixel rectangles of a token's ink on a px x px icon, and where the ring has to be to leave room for them.</summary>
    internal readonly record struct Layout(IReadOnlyList<Rectangle> Ink, Rectangle Bounds, float RingBandFraction);

    /// <summary>How a size draws its text: rows of the shapes, pixels per shape cell, gap between characters, ring band as a fraction of the icon.</summary>
    internal readonly record struct Style(int Rows, int CellW, int CellH, int Gap, float RingBandFraction);

    // '#' is ink. Five rows; the seven-row shapes repeat the two "vertical only" rows (1 and 3), see Rows7.
    static readonly Dictionary<char, string[]> Shapes = new()
    {
        ['0'] = new[] { "###", "#.#", "#.#", "#.#", "###" },
        ['1'] = new[] { ".#", "##", ".#", ".#", ".#" },
        ['2'] = new[] { "###", "..#", "###", "#..", "###" },
        ['3'] = new[] { "###", "..#", "###", "..#", "###" },
        ['4'] = new[] { "#.#", "#.#", "###", "..#", "..#" },
        ['5'] = new[] { "###", "#..", "###", "..#", "###" },
        ['6'] = new[] { "###", "#..", "###", "#.#", "###" },
        ['7'] = new[] { "###", "..#", "..#", ".#.", ".#." },
        ['8'] = new[] { "###", "#.#", "###", "#.#", "###" },
        ['9'] = new[] { "###", "#.#", "###", "..#", "###" },
        ['d'] = new[] { "..#", "..#", "###", "#.#", "###" },
        ['h'] = new[] { "#..", "#..", "###", "#.#", "#.#" },
        ['m'] = new[] { "#.#", "###", "#.#", "#.#", "#.#" },
    };

    /// <summary>The seven-row shapes: the five-row ones with rows 1 and 3 (vertical strokes only) repeated; "m" is drawn out.</summary>
    static string[] Rows7(char c) => c == 'm'
        ? new[] { "#.#", "###", "#.#", "#.#", "#.#", "#.#", "#.#" }
        : new[] { Shapes[c][0], Shapes[c][1], Shapes[c][1], Shapes[c][2], Shapes[c][3], Shapes[c][3], Shapes[c][4] };

    internal static Style StyleFor(int px) => px switch
    {
        < 20 => new Style(Rows: 5, CellW: 1, CellH: 1, Gap: 1, RingBandFraction: 0.125f),
        < 24 => new Style(Rows: 7, CellW: 1, CellH: 1, Gap: 1, RingBandFraction: 0.12f),
        < 28 => new Style(Rows: 7, CellW: 1, CellH: 1, Gap: 2, RingBandFraction: 0.11f),
        _ => new Style(Rows: 5, CellW: Math.Max(2, px / 16), CellH: Math.Max(2, px / 16), Gap: Math.Max(2, px / 16), RingBandFraction: 0.085f),
    };

    internal static bool CanDraw(string text) => text.Length is >= 1 and <= 3 && text.All(Shapes.ContainsKey);

    /// <summary>
    /// The ink of `text`, centred on the icon (an odd width or height sits half a pixel left/up of true
    /// centre: a whole-pixel grid cannot do better). Throws for anything the font does not have.
    /// </summary>
    internal static Layout For(string text, int px)
    {
        if (!CanDraw(text)) throw new ArgumentException($"no countdown glyphs for \"{text}\"", nameof(text));
        Style st = StyleFor(px);

        var chars = text.Select(c => st.Rows == 7 ? Rows7(c) : Shapes[c]).ToList();
        int width = chars.Sum(rows => rows[0].Length * st.CellW) + (chars.Count - 1) * st.Gap;
        int height = st.Rows * st.CellH;
        int left = (px - width) / 2, top = (px - height) / 2;

        var ink = new List<Rectangle>();
        int x = left;
        foreach (string[] rows in chars)
        {
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] == '#')
                        ink.Add(new Rectangle(x + c * st.CellW, top + r * st.CellH, st.CellW, st.CellH));
            x += rows[0].Length * st.CellW + st.Gap;
        }
        return new Layout(ink, new Rectangle(left, top, width, height), st.RingBandFraction);
    }
}

namespace RefractionLab.Logic;

public readonly record struct Rgb(byte R, byte G, byte B);

public enum ProbeVerdict { Present, Absent, Inconclusive }

/// <summary>
/// Random color grid used to decide whether this app's own window shows up in captured frames.
/// The caller reads back only the grid's pixels from a frame; nothing else is inspected.
/// </summary>
public static class ProbePattern
{
    public const int Cells = 8;
    public const double PresentAtLeast = 0.75;
    public const double AbsentAtMost = 0.40;
    private const int Tolerance = 40;

    // Saturated colors only, so a uniform wallpaper can match at most one of them (~1/6 of cells).
    private static readonly Rgb[] Palette =
    [
        new(244, 70, 83), new(62, 220, 133), new(72, 130, 248),
        new(248, 205, 63), new(211, 79, 225), new(54, 211, 222),
    ];

    public static Rgb[] Create(Random random)
    {
        var cells = new Rgb[Cells * Cells];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Palette[random.Next(Palette.Length)];
        return cells;
    }

    /// <param name="bgra">Tightly packed BGRA pixels of the (Cells*cellWidth) x (Cells*cellHeight) probe region.</param>
    public static double MatchFraction(ReadOnlySpan<byte> bgra, int cellWidth, int cellHeight, IReadOnlyList<Rgb> pattern)
    {
        if (cellWidth < 1 || cellHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(cellWidth), "Cells must be at least 1x1 pixel.");
        if (pattern.Count != Cells * Cells)
            throw new ArgumentException("Pattern must contain Cells*Cells entries.", nameof(pattern));

        int stride = Cells * cellWidth * 4;
        if (bgra.Length < stride * Cells * cellHeight)
            throw new ArgumentException("Pixel buffer is smaller than the probe region.", nameof(bgra));

        int matches = 0;
        for (int y = 0; y < Cells; y++)
        for (int x = 0; x < Cells; x++)
        {
            int offset = (y * cellHeight + cellHeight / 2) * stride + (x * cellWidth + cellWidth / 2) * 4;
            Rgb expected = pattern[y * Cells + x];
            if (Math.Abs(bgra[offset] - expected.B) <= Tolerance &&
                Math.Abs(bgra[offset + 1] - expected.G) <= Tolerance &&
                Math.Abs(bgra[offset + 2] - expected.R) <= Tolerance)
                matches++;
        }

        return matches / (double)(Cells * Cells);
    }

    public static ProbeVerdict Classify(double fraction) =>
        fraction >= PresentAtLeast ? ProbeVerdict.Present :
        fraction <= AbsentAtMost ? ProbeVerdict.Absent :
        ProbeVerdict.Inconclusive;
}

using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class ProbePatternTests
{
    private const int Cell = 6;

    // BGRA bytes of the probe region as a captured frame would show it.
    private static byte[] Frame(Func<int, int, Rgb> colorAt)
    {
        int side = ProbePattern.Cells * Cell;
        var bytes = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            Rgb c = colorAt(x, y);
            int i = (y * side + x) * 4;
            bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = 255;
        }
        return bytes;
    }

    private static byte[] Render(Rgb[] pattern) =>
        Frame((x, y) => pattern[(y / Cell) * ProbePattern.Cells + x / Cell]);

    private static double Measure(byte[] frame, Rgb[] pattern) =>
        ProbePattern.MatchFraction(frame, Cell, Cell, pattern);

    [Fact]
    public void FrameShowingThePatternIsPresent()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(1));
        double f = Measure(Render(pattern), pattern);
        Assert.Equal(1.0, f);
        Assert.Equal(ProbeVerdict.Present, ProbePattern.Classify(f));
    }

    [Fact]
    public void ColorNoiseWithinToleranceStillCountsAsPresent()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(2));
        var rng = new Random(3);
        byte[] frame = Render(pattern);
        for (int i = 0; i < frame.Length; i += 4)
            for (int c = 0; c < 3; c++)
                frame[i + c] = (byte)Math.Clamp(frame[i + c] + rng.Next(-15, 16), 0, 255);

        Assert.Equal(ProbeVerdict.Present, ProbePattern.Classify(Measure(frame, pattern)));
    }

    [Fact]
    public void BlackOrBlankProtectedFrameIsNeverPresent()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(4));
        foreach (Rgb blank in new[] { new Rgb(0, 0, 0), new Rgb(255, 255, 255), new Rgb(128, 128, 128) })
            Assert.Equal(ProbeVerdict.Absent, ProbePattern.Classify(Measure(Frame((_, _) => blank), pattern)));
    }

    [Fact]
    public void UniformWallpaperInAnyPaletteColorIsAbsent()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(5));
        foreach (Rgb color in pattern.Distinct())
            Assert.Equal(ProbeVerdict.Absent, ProbePattern.Classify(Measure(Frame((_, _) => color), pattern)));
    }

    [Fact]
    public void AnotherRandomPatternIsAbsentAcrossManySeeds()
    {
        for (int seed = 0; seed < 500; seed++)
        {
            Rgb[] shown = ProbePattern.Create(new Random(seed));
            Rgb[] expected = ProbePattern.Create(new Random(seed + 10_000));
            Assert.Equal(ProbeVerdict.Absent, ProbePattern.Classify(Measure(Render(shown), expected)));
        }
    }

    [Fact]
    public void HalfVisiblePatternIsInconclusiveNotAPass()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(6));
        int half = ProbePattern.Cells / 2;
        byte[] frame = Frame((x, y) => y / Cell < half ? pattern[(y / Cell) * ProbePattern.Cells + x / Cell] : new Rgb(0, 0, 0));
        double f = Measure(frame, pattern);
        Assert.InRange(f, 0.41, 0.74);
        Assert.Equal(ProbeVerdict.Inconclusive, ProbePattern.Classify(f));
    }

    [Theory]
    [InlineData(0.75, ProbeVerdict.Present)]
    [InlineData(0.749, ProbeVerdict.Inconclusive)]
    [InlineData(0.40, ProbeVerdict.Absent)]
    [InlineData(0.401, ProbeVerdict.Inconclusive)]
    public void ClassifyThresholds(double fraction, ProbeVerdict expected) =>
        Assert.Equal(expected, ProbePattern.Classify(fraction));

    [Fact]
    public void ShortBufferIsRejectedInsteadOfReadingOutOfRange()
    {
        Rgb[] pattern = ProbePattern.Create(new Random(7));
        Assert.Throws<ArgumentException>(() => ProbePattern.MatchFraction(new byte[16], Cell, Cell, pattern));
    }

    [Fact]
    public void PatternsDifferBetweenAttempts()
    {
        Assert.NotEqual(ProbePattern.Create(new Random(1)), ProbePattern.Create(new Random(2)));
    }
}

using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class DpiMathTests
{
    private static readonly double[] Scales = [1.0, 1.1, 1.25, 1.33, 1.5, 1.75, 2.0, 2.25, 2.5, 3.0, 3.5, 4.0];

    [Fact]
    public void ResultIsExactUnderBothRoundingAndTruncation()
    {
        foreach (double scale in Scales)
            for (int px = 1; px <= 4096; px++)
            {
                double back = DpiMath.DipsForPixels(px, scale) * scale;
                Assert.Equal(px, (int)Math.Floor(back));
                Assert.Equal(px, (int)Math.Round(back));
            }
    }

    [Fact]
    public void PlainDivisionWouldLoseAPixelSomewhere()
    {
        int lost = 0;
        foreach (double scale in Scales)
            for (int px = 1; px <= 4096; px++)
                if ((int)Math.Floor((float)(px / scale) * scale) != px)
                    lost++;
        Assert.True(lost > 0, "if this fails the nudge is unnecessary");
    }

    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(-3, 1.5)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void InvalidInputsGiveZero(int pixels, double scale) =>
        Assert.Equal(0f, DpiMath.DipsForPixels(pixels, scale));

    [Fact]
    public void AtOneHundredPercentTheNudgeIsAQuarterPixel() =>
        Assert.Equal(700.25f, DpiMath.DipsForPixels(700, 1.0));
}

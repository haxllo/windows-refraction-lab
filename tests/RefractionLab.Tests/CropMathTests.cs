using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class CropMathTests
{
    private static readonly PxRect Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void CenteredPanelMapsToFrameCoordinates()
    {
        var panel = new PxRect(0, 0, 700, 360);
        Assert.True(CropMath.TryToFrame(panel, 610, 360, Primary, out PxRect f));
        Assert.Equal(new PxRect(610, 360, 700, 360), f);
    }

    [Fact]
    public void SecondaryMonitorOffsetIsRemoved()
    {
        var right = new PxRect(1920, 0, 2560, 1440);
        Assert.True(CropMath.TryToFrame(new PxRect(10, 20, 50, 50), 2500, 300, right, out PxRect f));
        Assert.Equal(new PxRect(590, 320, 50, 50), f);
    }

    [Fact]
    public void MonitorLeftOfPrimaryHasNegativeOrigin()
    {
        var left = new PxRect(-1920, 0, 1920, 1080);
        Assert.True(CropMath.TryToFrame(new PxRect(0, 0, 100, 100), -1000, 200, left, out PxRect f));
        Assert.Equal(new PxRect(920, 200, 100, 100), f);
    }

    [Theory]
    [InlineData(-1, 100)]     // starts left of the frame
    [InlineData(100, -1)]     // starts above
    [InlineData(1221, 100)]   // 700 wide ends past 1920
    [InlineData(100, 721)]    // 360 tall ends past 1080
    public void AnythingOutsideTheCapturedDisplayIsRefused(int x, int y)
    {
        Assert.False(CropMath.TryToFrame(new PxRect(0, 0, 700, 360), x, y, Primary, out _));
    }

    [Fact]
    public void PaddedCropRefusedWhenPaddingLeavesTheDisplay()
    {
        var panel = new PxRect(0, 0, 700, 360);
        PxRect padded = CropMath.Inflate(panel, 12);
        Assert.True(CropMath.TryToFrame(padded, 612, 372, Primary, out _));
        Assert.False(CropMath.TryToFrame(padded, 8, 372, Primary, out _));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void ProbeFitsInsidePanelAtCommonScales(double scale)
    {
        int width = (int)Math.Round(700 * scale);
        int height = (int)Math.Round(360 * scale);
        PxRect probe = CropMath.ProbeRect(width, scale, out int cell);

        Assert.True(cell >= 4);
        Assert.Equal(ProbePattern.Cells * cell, probe.Width);
        Assert.True(probe.X >= 0 && probe.Y >= 0);
        Assert.True(probe.X + probe.Width <= width && probe.Y + probe.Height <= height);
    }
}

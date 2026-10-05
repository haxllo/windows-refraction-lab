using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class FramePacingTests
{
    [Theory]
    [InlineData(15, 66.6)]
    [InlineData(30, 33.3)]
    public void CappedRatesWaitOneFramePeriod(int fps, double expectedMs) =>
        Assert.Equal(expectedMs, FramePacing.MinInterval(fps).TotalMilliseconds, 0.1);

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    public void SixtyAndUpIsUncapped(int fps) => Assert.Equal(TimeSpan.Zero, FramePacing.MinInterval(fps));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InvalidRatesFallBackToDefault(int fps) =>
        Assert.Equal(FramePacing.MinInterval(FramePacing.DefaultFps), FramePacing.MinInterval(fps));

    [Fact]
    public void DefaultIsOneOfTheOfferedOptionsAndAboveTheOldTwelveFpsCap()
    {
        Assert.Contains(FramePacing.DefaultFps, FramePacing.Options);
        Assert.True(FramePacing.DefaultFps > 12);
    }
}

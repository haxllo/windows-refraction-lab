using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class FramePacingTests
{
    [Theory]
    [InlineData(15, 66.6)]
    [InlineData(30, 33.3)]
    [InlineData(60, 16.7)]
    public void CappedRatesWaitOneFramePeriod(int fps, double expectedMs) =>
        Assert.Equal(expectedMs, FramePacing.MinInterval(fps).TotalMilliseconds, 0.1);

    [Fact]
    public void UnlimitedHasNoMinimumInterval() =>
        Assert.Equal(TimeSpan.Zero, FramePacing.MinInterval(FramePacing.Unlimited));

    [Fact]
    public void SixtyIsARealCapNotUnlimited() =>
        Assert.True(FramePacing.MinInterval(60) > TimeSpan.Zero);

    [Theory]
    [InlineData(-5)]
    [InlineData(-60)]
    public void InvalidRatesFallBackToDefault(int fps) =>
        Assert.Equal(FramePacing.MinInterval(FramePacing.DefaultFps), FramePacing.MinInterval(fps));

    [Fact]
    public void OptionsGetFasterAndEndWithUnlimited()
    {
        Assert.Contains(FramePacing.DefaultFps, FramePacing.Options);
        Assert.True(FramePacing.DefaultFps > 12);
        Assert.Equal(FramePacing.Unlimited, FramePacing.Options[^1]);

        TimeSpan previous = TimeSpan.MaxValue;
        foreach (int fps in FramePacing.Options)
        {
            TimeSpan interval = FramePacing.MinInterval(fps);
            Assert.True(interval < previous, $"{fps}");
            previous = interval;
        }
    }

    [Fact]
    public void LabelsMatchWhatTheSelectorShows()
    {
        Assert.Equal("Unlimited", FramePacing.Label(FramePacing.Unlimited));
        Assert.Equal("60", FramePacing.Label(60));
    }
}

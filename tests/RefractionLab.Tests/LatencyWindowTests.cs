using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class LatencyWindowTests
{
    [Fact]
    public void ReportsAverageMaximumAndCount()
    {
        var window = new LatencyWindow();
        window.Add(2);
        window.Add(4);
        window.Add(12);

        LatencySummary s = window.Take();
        Assert.Equal(6, s.AvgMs, 6);
        Assert.Equal(12, s.MaxMs);
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void TakingResetsTheWindow()
    {
        var window = new LatencyWindow();
        window.Add(5);
        window.Take();
        Assert.Equal(default, window.Take());
    }

    [Fact]
    public void EmptyWindowReportsZerosNotNaN()
    {
        LatencySummary s = new LatencyWindow().Take();
        Assert.Equal(0, s.AvgMs);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void NegativeDelaysFromClockSkewAreClampedToZero()
    {
        var window = new LatencyWindow();
        window.Add(-3);
        Assert.Equal(new LatencySummary(0, 0, 1), window.Take());
    }

    [Fact]
    public void ConcurrentAddsAreNotLost()
    {
        var window = new LatencyWindow();
        Parallel.For(0, 8, _ => { for (int i = 0; i < 5_000; i++) window.Add(1); });
        LatencySummary s = window.Take();
        Assert.Equal(40_000, s.Count);
        Assert.Equal(1, s.AvgMs, 6);
    }
}

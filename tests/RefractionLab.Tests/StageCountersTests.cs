using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class StageCountersTests
{
    [Fact]
    public void RatesAreCountsPerSecondPerStage()
    {
        var counters = new StageCounters();
        counters.Add(Stage.Source, 120);
        counters.Add(Stage.Used, 60);
        for (int i = 0; i < 30; i++)
            counters.Add(Stage.Drawn);

        StageRates rates = counters.TakeRates(TimeSpan.FromSeconds(2));
        Assert.Equal(60, rates.Source);
        Assert.Equal(30, rates.Used);
        Assert.Equal(15, rates.Drawn);
    }

    [Fact]
    public void TakingRatesResetsTheCounts()
    {
        var counters = new StageCounters();
        counters.Add(Stage.Used, 10);
        counters.TakeRates(TimeSpan.FromSeconds(1));
        Assert.Equal(default, counters.TakeRates(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ZeroElapsedTimeGivesZeroRatesInsteadOfInfinity()
    {
        var counters = new StageCounters();
        counters.Add(Stage.Source, 5);
        Assert.Equal(default, counters.TakeRates(TimeSpan.Zero));
    }

    [Fact]
    public void ConcurrentAddsAreNotLost()
    {
        var counters = new StageCounters();
        Parallel.For(0, 8, _ => { for (int i = 0; i < 10_000; i++) counters.Add(Stage.Used); });
        Assert.Equal(80_000, counters.TakeRates(TimeSpan.FromSeconds(1)).Used);
    }
}

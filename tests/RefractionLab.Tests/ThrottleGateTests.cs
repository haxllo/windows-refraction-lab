using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class ThrottleGateTests
{
    private const double WindowsTickMs = 15.625; // default system timer resolution

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static ThrottleGate Gate(double intervalMs) => new() { Interval = Ms(intervalMs) };

    [Fact]
    public void FirstSignalFiresImmediately()
    {
        Assert.Equal(ThrottleAction.Fire, Gate(33).Signal(Ms(0), out _));
    }

    [Fact]
    public void ZeroIntervalFiresEverySignal()
    {
        ThrottleGate gate = Gate(0);
        for (int i = 0; i < 5; i++)
            Assert.Equal(ThrottleAction.Fire, gate.Signal(Ms(i), out _));
    }

    [Fact]
    public void SignalSoonAfterAFireArmsForTheRemainder()
    {
        ThrottleGate gate = Gate(33);
        gate.Signal(Ms(0), out _);

        Assert.Equal(ThrottleAction.Arm, gate.Signal(Ms(10), out TimeSpan armFor));
        Assert.Equal(23, armFor.TotalMilliseconds, 0.01);
    }

    [Fact]
    public void FurtherSignalsWhileArmedDoNothing()
    {
        ThrottleGate gate = Gate(33);
        gate.Signal(Ms(0), out _);
        gate.Signal(Ms(5), out _);
        Assert.Equal(ThrottleAction.Nothing, gate.Signal(Ms(8), out _));
        Assert.Equal(ThrottleAction.Nothing, gate.Signal(Ms(9), out _));
    }

    [Fact]
    public void TickWhenDueFiresTheTrailingChange()
    {
        ThrottleGate gate = Gate(33);
        gate.Signal(Ms(0), out _);
        gate.Signal(Ms(10), out _);
        Assert.Equal(ThrottleAction.Fire, gate.Tick(Ms(33), out _));
    }

    [Fact]
    public void TickBeforeDueRearms()
    {
        ThrottleGate gate = Gate(33);
        gate.Signal(Ms(0), out _);
        gate.Signal(Ms(10), out _);

        Assert.Equal(ThrottleAction.Arm, gate.Tick(Ms(20), out TimeSpan armFor));
        Assert.Equal(13, armFor.TotalMilliseconds, 0.01);
        Assert.Equal(ThrottleAction.Fire, gate.Tick(Ms(33), out _));
    }

    [Fact]
    public void StaleTickAfterAnInlineFireDoesNothing()
    {
        ThrottleGate gate = Gate(33);
        gate.Signal(Ms(0), out _);
        gate.Signal(Ms(10), out _);                                  // arms
        Assert.Equal(ThrottleAction.Fire, gate.Signal(Ms(40), out _)); // due before the timer ran
        Assert.Equal(ThrottleAction.Nothing, gate.Tick(Ms(43), out _)); // nothing new to deliver
    }

    [Fact]
    public void SlightlyEarlySignalCountsAsDue()
    {
        ThrottleGate gate = Gate(1000.0 / 60);
        gate.Signal(Ms(0), out _);
        Assert.Equal(ThrottleAction.Fire, gate.Signal(Ms(15), out _)); // 1.67 ms early, within tolerance
    }

    [Fact]
    public void ToleranceNeverExceedsAQuarterOfTheInterval()
    {
        ThrottleGate gate = Gate(4);                       // tolerance = 1 ms, not 2
        gate.Signal(Ms(0), out _);
        Assert.Equal(ThrottleAction.Arm, gate.Signal(Ms(2), out _));
    }

    [Fact]
    public void ChangingTheIntervalTakesEffectOnTheNextSignal()
    {
        ThrottleGate gate = Gate(100);
        gate.Signal(Ms(0), out _);
        gate.Interval = Ms(10);
        Assert.Equal(ThrottleAction.Fire, gate.Signal(Ms(12), out _));
        gate.Interval = TimeSpan.FromMilliseconds(-5);
        Assert.Equal(TimeSpan.Zero, gate.Interval);
    }

    [Fact]
    public void AfterALongIdleThereIsNoBurstOfCatchUpFires()
    {
        ThrottleGate gate = Gate(16.7);
        gate.Signal(Ms(0), out _);
        Assert.Equal(ThrottleAction.Fire, gate.Signal(Ms(5000), out _));
        Assert.Equal(ThrottleAction.Arm, gate.Signal(Ms(5003), out _));
    }

    // Drives a gate with a fixed-rate source and a timer that, like Windows', can only fire on tick boundaries.
    // timerTickMs = 0 models a perfect timer.
    private static int Simulate(
        double sourceHz, double capHz, double seconds,
        out double lastFireMs, out double lastSignalMs, out int signals, double timerTickMs = 0)
    {
        ThrottleGate gate = Gate(capHz <= 0 ? 0 : 1000.0 / capHz);
        double step = 1000.0 / sourceHz, timerAt = double.MaxValue;
        int fires = 0, count = (int)Math.Round(sourceHz * seconds), sigCount = 0;
        double lastFire = 0, lastSignal = 0;

        double ArmAt(double now, TimeSpan armFor)
        {
            double due = now + armFor.TotalMilliseconds;
            return timerTickMs <= 0 ? due : Math.Ceiling(due / timerTickMs) * timerTickMs;
        }

        void RunTimer()
        {
            double now = timerAt;
            timerAt = double.MaxValue;
            ThrottleAction a = gate.Tick(Ms(now), out TimeSpan armFor);
            if (a == ThrottleAction.Fire) { fires++; lastFire = now; }
            else if (a == ThrottleAction.Arm) timerAt = ArmAt(now, armFor);
        }

        for (int i = 0; i < count; i++)
        {
            double t = i * step;
            while (timerAt <= t)
                RunTimer();

            ThrottleAction action = gate.Signal(Ms(t), out TimeSpan arm);
            sigCount++;
            lastSignal = t;
            if (action == ThrottleAction.Fire) { fires++; lastFire = t; }
            else if (action == ThrottleAction.Arm) timerAt = ArmAt(t, arm);
        }

        while (timerAt != double.MaxValue)
            RunTimer();

        lastFireMs = lastFire;
        lastSignalMs = lastSignal;
        signals = sigCount;
        return fires;
    }

    [Theory]
    [InlineData(144, 60, 60)]
    [InlineData(120, 30, 30)]
    [InlineData(60, 30, 30)]
    public void FastSourceIsLimitedToTheCap(double sourceHz, double capHz, double expectedFps)
    {
        int fires = Simulate(sourceHz, capHz, 10, out _, out _, out _);
        Assert.InRange(fires / 10.0, expectedFps * 0.97, expectedFps * 1.03);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(40, 60)]
    [InlineData(24, 60)]
    [InlineData(60, 0)]
    [InlineData(144, 0)]
    public void SourceAtOrBelowTheCapPassesEveryFrame(double sourceHz, double capHz)
    {
        int fires = Simulate(sourceHz, capHz, 10, out _, out _, out int signals);
        Assert.Equal(signals, fires);
    }

    // Regression: with a source only a little faster than the cap, an armed timer that overshoots
    // used to lose the race to the next frame, so every second frame was dropped (~35 fps at 70 Hz / 60).
    [Theory]
    [InlineData(70, 60)]
    [InlineData(79, 60)]
    [InlineData(100, 60)]
    [InlineData(90, 30)]
    [InlineData(144, 60)]
    public void SourceJustAboveTheCapStillReachesTheCapWithACoarseTimer(double sourceHz, double capHz)
    {
        int fires = Simulate(sourceHz, capHz, 10, out _, out _, out _, WindowsTickMs);
        Assert.InRange(fires / 10.0, capHz * 0.92, capHz * 1.03);
    }

    [Theory]
    [InlineData(70, 60)]
    [InlineData(144, 30)]
    public void ACoarseTimerNeverLetsTheRateExceedTheCap(double sourceHz, double capHz)
    {
        int fires = Simulate(sourceHz, capHz, 10, out _, out _, out _, WindowsTickMs);
        Assert.True(fires / 10.0 <= capHz * 1.03, $"{fires / 10.0} fps exceeds the {capHz} cap");
    }

    [Fact]
    public void TheLastChangeIsAlwaysDeliveredEventually()
    {
        Simulate(240, 30, 2, out double lastFire, out double lastSignal, out _);
        Assert.True(lastFire >= lastSignal, $"last fire {lastFire} ms is before the last signal {lastSignal} ms");
    }

    [Fact]
    public void TheLastChangeIsDeliveredEvenWithACoarseTimer()
    {
        Simulate(70, 30, 2, out double lastFire, out double lastSignal, out _, WindowsTickMs);
        Assert.True(lastFire >= lastSignal, $"last fire {lastFire} ms is before the last signal {lastSignal} ms");
    }
}

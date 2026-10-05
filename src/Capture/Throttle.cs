using RefractionLab.Logic;
using System.Diagnostics;

namespace RefractionLab.Capture;

/// <summary>
/// Runs an action at most once per interval from any thread, always delivering the last signal.
/// Frames are judged on arrival, so the steady-state rate does not depend on timer resolution;
/// the timer only covers the trailing edge when the content stops changing.
/// </summary>
internal sealed class Throttle : IDisposable
{
    private readonly ThrottleGate _gate = new();
    private readonly Action _fire;
    private readonly Timer _timer;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private volatile bool _disposed;

    public Throttle(TimeSpan interval, Action fire)
    {
        _gate.Interval = interval;
        _fire = fire;
        _timer = new Timer(_ => Apply(_gate.Tick(Now(), out TimeSpan armFor), armFor), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan Interval
    {
        set => _gate.Interval = value;
    }

    public void Signal() => Apply(_gate.Signal(Now(), out TimeSpan armFor), armFor);

    private TimeSpan Now() => Stopwatch.GetElapsedTime(_origin);

    private void Apply(ThrottleAction action, TimeSpan armFor)
    {
        if (_disposed)
            return;

        if (action == ThrottleAction.Fire)
            _fire();
        else if (action == ThrottleAction.Arm)
        {
            try { _timer.Change(armFor, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

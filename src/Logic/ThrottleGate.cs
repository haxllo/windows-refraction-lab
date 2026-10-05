namespace RefractionLab.Logic;

public enum ThrottleAction { Nothing, Fire, Arm }

/// <summary>
/// Decides when to act on a stream of "something changed" signals at most once per interval, without
/// ever dropping the last change. Pure: the caller supplies the clock and owns the timer.
///
/// Signal: a change happened. Fire = act now; Arm = call Tick after armFor; Nothing = a timer is already pending.
/// Tick:   the armed timer elapsed. Same return values.
/// </summary>
public sealed class ThrottleGate
{
    private static readonly TimeSpan MaxEarly = TimeSpan.FromMilliseconds(2);

    private readonly object _lock = new();
    private TimeSpan _interval;
    private TimeSpan _lastFire;
    private bool _hasFired;
    private bool _dirty;
    private bool _armed;

    public TimeSpan Interval
    {
        get { lock (_lock) return _interval; }
        set { lock (_lock) _interval = value < TimeSpan.Zero ? TimeSpan.Zero : value; }
    }

    public ThrottleAction Signal(TimeSpan now, out TimeSpan armFor)
    {
        lock (_lock)
        {
            _dirty = true;
            return Evaluate(now, out armFor);
        }
    }

    public ThrottleAction Tick(TimeSpan now, out TimeSpan armFor)
    {
        lock (_lock)
        {
            _armed = false;
            return Evaluate(now, out armFor);
        }
    }

    private ThrottleAction Evaluate(TimeSpan now, out TimeSpan armFor)
    {
        armFor = TimeSpan.Zero;
        if (!_dirty)
            return ThrottleAction.Nothing;

        TimeSpan wait = Remaining(now);
        if (wait <= TimeSpan.Zero)
        {
            _dirty = false;
            _lastFire = now;
            _hasFired = true;
            return ThrottleAction.Fire;
        }

        if (_armed)
            return ThrottleAction.Nothing;
        _armed = true;
        armFor = wait;
        return ThrottleAction.Arm;
    }

    // A signal arriving slightly early (display-sync jitter) counts as due, so a 60 fps cap
    // on a 60 Hz source passes every frame instead of waiting a whole extra timer tick.
    private TimeSpan Remaining(TimeSpan now)
    {
        if (!_hasFired || _interval <= TimeSpan.Zero)
            return TimeSpan.Zero;

        TimeSpan tolerance = TimeSpan.FromTicks(Math.Min(MaxEarly.Ticks, _interval.Ticks / 4));
        TimeSpan wait = _interval - (now - _lastFire);
        return wait <= tolerance ? TimeSpan.Zero : wait;
    }
}

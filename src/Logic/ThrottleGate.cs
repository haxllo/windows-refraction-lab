namespace RefractionLab.Logic;

public enum ThrottleAction { Nothing, Fire, Arm }

/// <summary>
/// Decides when to act on a stream of "something changed" signals at most once per interval, without
/// ever dropping the last change. Pure: the caller supplies the clock and owns the timer.
///
/// Signal: a change happened. Fire = act now; Arm = call Tick after armFor; Nothing = a timer is already pending.
/// Tick:   the armed timer elapsed. Same return values.
///
/// Fires follow a fixed schedule (next = previous due + interval) rather than "last fire + interval", so a
/// late timer or frame does not push every later frame back. That keeps a source slightly faster than the cap
/// at the cap even though timers overshoot by up to a system tick.
/// </summary>
public sealed class ThrottleGate
{
    private static readonly TimeSpan MaxEarly = TimeSpan.FromMilliseconds(2);

    private readonly object _lock = new();
    private TimeSpan _interval;
    private TimeSpan _nextDue;
    private bool _hasFired;
    private bool _dirty;
    private bool _armed;

    public TimeSpan Interval
    {
        get { lock (_lock) return _interval; }
        set
        {
            lock (_lock)
            {
                TimeSpan next = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                _nextDue += next - _interval; // _nextDue is last fire + interval, so move it with the interval
                _interval = next;
            }
        }
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
            Advance(now);
            return ThrottleAction.Fire;
        }

        if (_armed)
            return ThrottleAction.Nothing;
        _armed = true;
        armFor = wait;
        return ThrottleAction.Arm;
    }

    // Stay on the schedule, but re-anchor after idle (or a first fire) so there is no burst of catch-up fires.
    private void Advance(TimeSpan now)
    {
        if (!_hasFired || now - _nextDue >= _interval)
            _nextDue = now;
        _nextDue += _interval;
        _hasFired = true;
    }

    // A signal arriving slightly early (display-sync jitter) counts as due.
    private TimeSpan Remaining(TimeSpan now)
    {
        if (!_hasFired || _interval <= TimeSpan.Zero)
            return TimeSpan.Zero;

        TimeSpan tolerance = TimeSpan.FromTicks(Math.Min(MaxEarly.Ticks, _interval.Ticks / 4));
        TimeSpan wait = _nextDue - now;
        return wait <= tolerance ? TimeSpan.Zero : wait;
    }
}

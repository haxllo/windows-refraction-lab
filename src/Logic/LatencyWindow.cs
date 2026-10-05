namespace RefractionLab.Logic;

public readonly record struct LatencySummary(double AvgMs, double MaxMs, int Count);

/// <summary>Thread-safe average and maximum of a stream of delays, read and reset together once a second.</summary>
public sealed class LatencyWindow
{
    private readonly object _lock = new();
    private double _sum, _max;
    private int _count;

    public void Add(double ms)
    {
        ms = Math.Max(0, ms);
        lock (_lock)
        {
            _sum += ms;
            _max = Math.Max(_max, ms);
            _count++;
        }
    }

    public LatencySummary Take()
    {
        lock (_lock)
        {
            var summary = _count == 0 ? default : new LatencySummary(_sum / _count, _max, _count);
            _sum = _max = 0;
            _count = 0;
            return summary;
        }
    }
}

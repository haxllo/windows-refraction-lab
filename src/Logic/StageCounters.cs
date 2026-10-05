namespace RefractionLab.Logic;

public enum Stage { Source, Used, Drawn }

public readonly record struct StageRates(double Source, double Used, double Drawn);

/// <summary>Thread-safe per-stage event counts, read and reset together once a second.</summary>
public sealed class StageCounters
{
    private long _source, _used, _drawn;

    public void Add(Stage stage, long count = 1)
    {
        switch (stage)
        {
            case Stage.Source: Interlocked.Add(ref _source, count); break;
            case Stage.Used: Interlocked.Add(ref _used, count); break;
            case Stage.Drawn: Interlocked.Add(ref _drawn, count); break;
        }
    }

    public StageRates TakeRates(TimeSpan elapsed)
    {
        long source = Interlocked.Exchange(ref _source, 0);
        long used = Interlocked.Exchange(ref _used, 0);
        long drawn = Interlocked.Exchange(ref _drawn, 0);
        double seconds = elapsed.TotalSeconds;
        return seconds <= 0 ? default : new StageRates(source / seconds, used / seconds, drawn / seconds);
    }
}

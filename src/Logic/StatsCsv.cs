using System.Globalization;
using System.Text;

namespace RefractionLab.Logic;

/// <summary>One second of the numbers the panel already shows. Contains no screen content or window details.</summary>
public readonly record struct RenderTiming(double PaintAvgMs, double BlitAvgMs, double PresentAvgMs, double TotalMaxMs);

public readonly record struct StatsSample(
    string Api,
    string MaxFps,
    int DisplayHz,
    int BendPct,
    double Source,
    double Used,
    double Drawn,
    double AgeMs,
    double CopyMs,
    double DrawMs,
    LatencySummary UiQueue,
    LatencySummary RenderLag,
    double? DeviceLockMs,
    double CpuPct,
    long WorkingSetMb,
    bool? ProtectedMasked,   // null when the capture API has no such signal (Windows Graphics Capture)
    RenderTiming? Render = null);

/// <summary>
/// CSV formatting for the stats log. Always invariant-culture and ASCII, so the file reads the same
/// on every machine and the byte count of a line equals its length.
/// </summary>
public static class StatsCsv
{
    public const string Header =
        "utc,elapsed_s,api,max_fps,display_hz,bend_pct,source_per_s,used_per_s,drawn_per_s," +
        "age_ms,copy_ms,draw_ms,ui_queue_avg_ms,ui_queue_max_ms,invalidate_draw_avg_ms,invalidate_draw_max_ms," +
        "device_lock_ms,cpu_pct_of_1_core,working_set_mb,protected_masked," +
        "render_paint_avg_ms,render_blit_avg_ms,render_present_avg_ms,render_total_max_ms,event";

    public static int ColumnCount { get; } = Header.Split(',').Length;

    public static string Row(DateTimeOffset utc, double elapsedSeconds, StatsSample s)
    {
        var f = new string[ColumnCount];
        f[0] = Time(utc);
        f[1] = Num(elapsedSeconds, "0.0");
        f[2] = Escape(s.Api);
        f[3] = Escape(s.MaxFps);
        f[4] = s.DisplayHz > 0 ? s.DisplayHz.ToString(CultureInfo.InvariantCulture) : string.Empty;
        f[5] = s.BendPct.ToString(CultureInfo.InvariantCulture);
        f[6] = Num(s.Source, "0.0");
        f[7] = Num(s.Used, "0.0");
        f[8] = Num(s.Drawn, "0.0");
        f[9] = Num(s.AgeMs, "0");
        f[10] = Num(s.CopyMs, "0.00");
        f[11] = Num(s.DrawMs, "0.00");
        f[12] = Num(s.UiQueue.AvgMs, "0.0");
        f[13] = Num(s.UiQueue.MaxMs, "0.0");
        f[14] = Num(s.RenderLag.AvgMs, "0.0");
        f[15] = Num(s.RenderLag.MaxMs, "0.0");
        f[16] = s.DeviceLockMs is double lockMs ? Num(lockMs, "0.00") : string.Empty;
        f[17] = Num(s.CpuPct, "0.0");
        f[18] = s.WorkingSetMb.ToString(CultureInfo.InvariantCulture);
        f[19] = s.ProtectedMasked is bool masked ? (masked ? "1" : "0") : string.Empty;
        f[20] = s.Render is { } r ? Num(r.PaintAvgMs, "0.0") : string.Empty;
        f[21] = s.Render is { } r2 ? Num(r2.BlitAvgMs, "0.0") : string.Empty;
        f[22] = s.Render is { } r3 ? Num(r3.PresentAvgMs, "0.0") : string.Empty;
        f[23] = s.Render is { } r4 ? Num(r4.TotalMaxMs, "0.0") : string.Empty;
        f[24] = string.Empty;
        return string.Join(',', f);
    }

    /// <summary>A row that carries only a timestamp, the api and a note (start, stop, setting changes).</summary>
    public static string Event(DateTimeOffset utc, double elapsedSeconds, string api, string text)
    {
        var f = new string[ColumnCount];
        Array.Fill(f, string.Empty);
        f[0] = Time(utc);
        f[1] = Num(elapsedSeconds, "0.0");
        f[2] = Escape(api);
        f[ColumnCount - 1] = Escape(text);
        return string.Join(',', f);
    }

    private static string Time(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Num(double value, string format) =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (c == '·') sb.Append('|');
            else if (c == '…') sb.Append("...");
            else if (c == '→') sb.Append("->");
            else if (c < ' ' || c > '~') sb.Append('?');
            else sb.Append(c);
        }

        string ascii = sb.ToString();
        return ascii.Contains(',') || ascii.Contains('"')
            ? "\"" + ascii.Replace("\"", "\"\"") + "\""
            : ascii;
    }
}

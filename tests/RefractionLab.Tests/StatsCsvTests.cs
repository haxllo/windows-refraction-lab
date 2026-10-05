using System.Globalization;
using System.Text.RegularExpressions;
using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class StatsCsvTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 3, 7, 31, 481, TimeSpan.Zero);

    private static StatsSample Sample(double? deviceLock = 0.4, int displayHz = 79, string api = "dxgi", RenderTiming? render = null) => new(
        api, "60", displayHz, 50,
        69.8, 20.2, 19.9, 3, 0.31, 1.25,
        new LatencySummary(1.4, 12.9, 20), new LatencySummary(2.2, 15.0, 20),
        deviceLock, 17.5, 139, false, render);

    // Minimal RFC 4180 field splitter, enough to prove escaping round-trips.
    internal static List<string> Split(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    [Fact]
    public void HeaderRowsAndEventsAllHaveTheSameColumnCount()
    {
        Assert.Equal(25, StatsCsv.ColumnCount);
        Assert.Equal(StatsCsv.ColumnCount, Split(StatsCsv.Header).Count);
        Assert.Equal(StatsCsv.ColumnCount, Split(StatsCsv.Row(At, 12.3, Sample())).Count);
        Assert.Equal(StatsCsv.ColumnCount, Split(StatsCsv.Event(At, 0, "dxgi", "start max_fps=60")).Count);
    }

    [Fact]
    public void RowValuesLandInTheColumnsTheHeaderNames()
    {
        string[] names = Split(StatsCsv.Header).ToArray();
        string[] values = Split(StatsCsv.Row(At, 12.34, Sample())).ToArray();
        string V(string column) => values[Array.IndexOf(names, column)];

        Assert.Equal("2026-10-05T03:07:31.481Z", V("utc"));
        Assert.Equal("12.3", V("elapsed_s"));
        Assert.Equal("dxgi", V("api"));
        Assert.Equal("60", V("max_fps"));
        Assert.Equal("79", V("display_hz"));
        Assert.Equal("50", V("bend_pct"));
        Assert.Equal("69.8", V("source_per_s"));
        Assert.Equal("20.2", V("used_per_s"));
        Assert.Equal("19.9", V("drawn_per_s"));
        Assert.Equal("3", V("age_ms"));
        Assert.Equal("0.31", V("copy_ms"));
        Assert.Equal("1.25", V("draw_ms"));
        Assert.Equal("1.4", V("ui_queue_avg_ms"));
        Assert.Equal("12.9", V("ui_queue_max_ms"));
        Assert.Equal("2.2", V("invalidate_draw_avg_ms"));
        Assert.Equal("15.0", V("invalidate_draw_max_ms"));
        Assert.Equal("0.40", V("device_lock_ms"));
        Assert.Equal("17.5", V("cpu_pct_of_1_core"));
        Assert.Equal("139", V("working_set_mb"));
        Assert.Equal("0", V("protected_masked"));
        Assert.Equal("", V("render_paint_avg_ms"));       // not the render-thread path
        Assert.Equal("", V("render_total_max_ms"));
        Assert.Equal("", V("event"));
    }

    [Fact]
    public void RenderThreadTimingsLandInTheirOwnColumns()
    {
        string[] names = Split(StatsCsv.Header).ToArray();
        string[] values = Split(StatsCsv.Row(At, 3, Sample(render: new RenderTiming(24.31, 9.84, 0.46, 178.15)))).ToArray();
        string V(string column) => values[Array.IndexOf(names, column)];

        Assert.Equal(StatsCsv.ColumnCount, values.Length);
        Assert.Equal("24.3", V("render_paint_avg_ms"));
        Assert.Equal("9.8", V("render_blit_avg_ms"));
        Assert.Equal("0.5", V("render_present_avg_ms"));
        Assert.Equal("178.2", V("render_total_max_ms"));
        Assert.Equal("", V("event"));
        Assert.Equal("0", V("protected_masked"));          // neighbours are undisturbed
    }

    [Fact]
    public void TheEventColumnStaysLast()
    {
        string[] names = Split(StatsCsv.Header).ToArray();
        Assert.Equal("event", names[^1]);
        Assert.Equal(names.Length - 1, Array.IndexOf(names, "event"));
    }

    [Fact]
    public void NumbersUseADotWhateverTheCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string row = StatsCsv.Row(At, 1234.5, Sample());
            Assert.Contains("1234.5", row);
            Assert.Contains("69.8", row);
            Assert.Equal(StatsCsv.ColumnCount, Split(row).Count); // a decimal comma would have added columns
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void UnknownValuesAreLeftEmptyRatherThanZero()
    {
        string[] names = Split(StatsCsv.Header).ToArray();
        string[] values = Split(StatsCsv.Row(At, 1, Sample(deviceLock: null, displayHz: 0))).ToArray();
        Assert.Equal("", values[Array.IndexOf(names, "device_lock_ms")]);
        Assert.Equal("", values[Array.IndexOf(names, "display_hz")]);
    }

    [Fact]
    public void NonFiniteNumbersDoNotBreakTheRow()
    {
        StatsSample s = Sample() with { Source = double.NaN, CpuPct = double.PositiveInfinity };
        string[] row = Split(StatsCsv.Row(At, 1, s)).ToArray();
        Assert.Equal(StatsCsv.ColumnCount, row.Length);
        Assert.Equal("", row[Array.IndexOf(Split(StatsCsv.Header).ToArray(), "source_per_s")]);
    }

    [Fact]
    public void EventTextIsEscapedAndReducedToAscii()
    {
        string text = "stop: Capture stopped, \"user\"\r\nnext · done … a → b é";
        string line = StatsCsv.Event(At, 5, "wgc", text);

        List<string> fields = Split(line);
        Assert.Equal(StatsCsv.ColumnCount, fields.Count);
        Assert.Equal("stop: Capture stopped, \"user\"  next | done ... a -> b ?", fields[^1]);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void EventRowsCarryOnlyIdentityAndTheNote()
    {
        List<string> fields = Split(StatsCsv.Event(At, 5, "wgc", "refracting"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", fields[0]);
        Assert.Equal("5.0", fields[1]);
        Assert.Equal("wgc", fields[2]);
        Assert.All(fields.Skip(3).Take(StatsCsv.ColumnCount - 4), f => Assert.Equal("", f));
        Assert.Equal("refracting", fields[^1]);
    }

    [Fact]
    public void ARowNeverContainsAnythingButNumbersAndTheDeclaredLabels()
    {
        string row = StatsCsv.Row(At, 1, Sample());
        Assert.Matches(new Regex(@"^[0-9TZ:.\-,a-z]+$"), row);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RefractionLab.Logic;

/// <summary>
/// Opt-in, local-only CSV log of the numbers the panel already shows: one file per capture session,
/// flushed on every row so it can be tailed live. It never overwrites or appends to an existing file,
/// is capped in size, and a write failure only stops logging (and says why); it never reaches capture.
/// </summary>
public sealed class StatsLogFile : IDisposable
{
    public const long MaxBytes = 5L * 1024 * 1024;
    private const int NewLineBytes = 2;

    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly string _api;
    private long _bytes;
    private bool _closed;
    private string? _failure;

    private StatsLogFile(Stream stream, string path, string api)
    {
        FilePath = path;
        _api = api;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n" };
        TryWriteLine(StatsCsv.Header);
    }

    public string FilePath { get; }

    /// <summary>Why logging stopped (an exception type or "size limit reached"), or null while it is healthy.</summary>
    public string? Failure { get { lock (_lock) return _failure; } }

    public double ElapsedSeconds => Stopwatch.GetElapsedTime(_origin).TotalSeconds;

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RefractionLab", "logs");

    /// <summary>The kinds of failure that creating or writing a log can produce; callers treat these as "no log".</summary>
    public static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException;

    public static StatsLogFile Create(string directory, string api, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        string stamp = now.LocalDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"stats-{SafeName(api)}-{stamp}.csv");

        // CreateNew never overwrites or appends; FileShare.Read lets tail tools read the file while it is written.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            return ForStream(stream, path, api);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // The file name is sanitized in Create; event rows keep the caller's label so they match sample rows.
    internal static StatsLogFile ForStream(Stream stream, string path, string api) =>
        new(stream, path, api);

    public void WriteSample(StatsSample sample) =>
        Write(StatsCsv.Row(DateTimeOffset.UtcNow, ElapsedSeconds, sample));

    public void WriteEvent(string text) =>
        Write(StatsCsv.Event(DateTimeOffset.UtcNow, ElapsedSeconds, _api, text));

    private void Write(string line)
    {
        lock (_lock)
        {
            if (_closed || _failure is not null)
                return;

            if (_bytes + line.Length + NewLineBytes > MaxBytes)
            {
                TryWriteLine(StatsCsv.Event(DateTimeOffset.UtcNow, ElapsedSeconds, _api, "size_limit reached; logging stopped"));
                _failure ??= "size limit reached";
                CloseWriter();
                return;
            }

            TryWriteLine(line);
        }
    }

    // Lines are ASCII (see StatsCsv), so length is the byte count.
    private void TryWriteLine(string line)
    {
        try
        {
            _writer.WriteLine(line);
            _writer.Flush(); // explicit, inside the guard: a failing disk must not throw out of the constructor
            _bytes += line.Length + NewLineBytes;
        }
        catch (Exception ex) when (IsIoFailure(ex) || ex is ObjectDisposedException)
        {
            _failure ??= ex.GetType().Name;
            CloseWriter();
        }
    }

    private void CloseWriter()
    {
        if (_closed)
            return;
        _closed = true;
        try { _writer.Dispose(); }
        catch (Exception ex) when (IsIoFailure(ex)) { _failure ??= ex.GetType().Name; }
    }

    public void Dispose()
    {
        lock (_lock)
            CloseWriter();
    }

    private static string SafeName(string api)
    {
        var sb = new StringBuilder();
        foreach (char c in api)
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.Length == 0 ? "x" : sb.ToString();
    }
}

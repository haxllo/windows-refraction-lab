using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public sealed class StatsLogFileTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 3, 7, 31, 481, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rl-tests-" + Guid.NewGuid().ToString("N"), "logs");

    public void Dispose()
    {
        string root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static StatsSample Sample() => new(
        "wgc", "60", 79, 50, 70, 20, 20, 1, 0.3, 1.0,
        new LatencySummary(1, 2, 20), new LatencySummary(1, 2, 20), null, 12, 139, false);

    // Reads the way a tail tool does: while the writer still has the file open.
    private static string[] ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void CreatesTheFolderAndAFileStartingWithTheHeader()
    {
        using StatsLogFile log = StatsLogFile.Create(_dir, "wgc", At);

        Assert.True(File.Exists(log.FilePath));
        Assert.Equal(_dir, Path.GetDirectoryName(log.FilePath));
        Assert.Matches(@"^stats-wgc-\d{8}-\d{6}-\d{3}\.csv$", Path.GetFileName(log.FilePath));
        Assert.Equal(StatsCsv.Header, ReadWhileOpen(log.FilePath)[0]);
        Assert.Null(log.Failure);
    }

    [Fact]
    public void RowsAreOnDiskImmediatelyNotOnlyWhenTheLogCloses()
    {
        using StatsLogFile log = StatsLogFile.Create(_dir, "wgc", At);
        log.WriteEvent("start max_fps=60");
        log.WriteSample(Sample());

        string[] lines = ReadWhileOpen(log.FilePath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("start max_fps=60", lines[1]);
        Assert.All(lines, l => Assert.Equal(StatsCsv.ColumnCount, StatsCsvTests.Split(l).Count));
    }

    [Fact]
    public void NeverOverwritesOrAppendsToAnExistingFile()
    {
        using StatsLogFile first = StatsLogFile.Create(_dir, "wgc", At);
        first.WriteEvent("keep me");
        string before = string.Join("\n", ReadWhileOpen(first.FilePath));

        Assert.Throws<IOException>(() => StatsLogFile.Create(_dir, "wgc", At)); // same millisecond, same name
        Assert.Equal(before, string.Join("\n", ReadWhileOpen(first.FilePath)));
    }

    [Fact]
    public void TheApiNameCannotEscapeTheLogFolder()
    {
        using StatsLogFile log = StatsLogFile.Create(_dir, @"..\..\evil/x", At);
        Assert.Equal(_dir, Path.GetDirectoryName(log.FilePath));
        Assert.Matches(@"^stats-evilx-", Path.GetFileName(log.FilePath));
    }

    [Fact]
    public void AnUnusableLocationFailsWithAnExceptionTheAppHandles()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "i-am-a-file");
        File.WriteAllText(blocker, "x");

        Exception? ex = Record.Exception(() => StatsLogFile.Create(Path.Combine(blocker, "sub"), "wgc", At));
        Assert.NotNull(ex);
        Assert.True(StatsLogFile.IsIoFailure(ex!), ex!.GetType().Name);
    }

    [Fact]
    public void StopsAtTheSizeLimitWithAFinalNoteAndSaysWhy()
    {
        using StatsLogFile log = StatsLogFile.Create(_dir, "wgc", At);
        for (int i = 0; i < 200_000 && log.Failure is null; i++)
            log.WriteSample(Sample());

        Assert.Equal("size limit reached", log.Failure);
        long size = new FileInfo(log.FilePath).Length;
        Assert.InRange(size, StatsLogFile.MaxBytes - 400, StatsLogFile.MaxBytes + 400);
        Assert.Contains("size_limit", ReadWhileOpen(log.FilePath)[^1]);

        log.WriteSample(Sample()); // further writes are ignored
        Assert.Equal(size, new FileInfo(log.FilePath).Length);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() => throw new IOException("disk full");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
    }

    [Fact]
    public void AWriteFailureStopsLoggingQuietlyAndNeverThrowsToTheCaller()
    {
        StatsLogFile log = StatsLogFile.ForStream(new FailingStream(), "x.csv", "wgc");

        Assert.Equal("IOException", log.Failure);
        log.WriteSample(Sample());
        log.WriteEvent("stop");
        log.Dispose();
        log.Dispose();
    }

    [Fact]
    public void WritesAfterDisposeAreIgnoredAndDisposeIsRepeatable()
    {
        StatsLogFile log = StatsLogFile.Create(_dir, "wgc", At);
        log.WriteEvent("one");
        log.Dispose();
        log.Dispose();

        log.WriteSample(Sample());
        Assert.Equal(2, File.ReadAllLines(log.FilePath).Length); // header + "one"
        Assert.Null(log.Failure);
    }
}

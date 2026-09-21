using System.Globalization;
using System.Text;
using System.Threading.Channels;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The one background consumer every disk write in this app goes through (Codex
/// review High #4): the claude.exe stdout/stderr pumps and request completion must
/// never block on disk I/O, so raw-log and window-shape.csv writes no longer touch
/// the filesystem directly -- they enqueue onto one of two bounded, non-blocking
/// channels that a single dedicated background task drains.
///
/// Raw log queue: bounded to 10,000 entries, DropOldest under backpressure -- it is
/// a diagnostic log, lossy by design under sustained overload is an acceptable
/// trade for never blocking a pipe reader.
///
/// CSV queue: also bounded to 10,000, but DropWrite (never evicts an already-queued
/// row, so history stays contiguous) plus an explicit drop counter that gets logged
/// on every drop -- window-shape.csv feeds the forecasting pattern-learning backlog
/// (docs/backlog.md), so a silent gap is unacceptable even though a *counted* drop
/// under extreme backpressure is preferable to blocking a poll on a stuck disk.
///
/// Retention (Codex review Medium #10; docs/statistics.md decision 1 lowered the CSV figure):
/// raw daily logs are capped at 20MB/file and kept 7 days; window-shape.csv is rotated monthly
/// (window-shape-YYYY-MM.csv) and kept 60 days -- see CsvReplay.ReadRowsForWarmStart for the
/// reader side. cycles.csv and hourly-YYYY.csv (docs/statistics.md) are the long run the raw
/// window-shape detail used to be the only record of, so they are kept forever and never touched
/// by this sweep -- SweepRetentionOnce only ever globs "window-shape-*.csv".
/// </summary>
public sealed class DiskLogSink : IAsyncDisposable
{
    const int QueueCapacity = 10_000;
    const long RawFileCapBytes = 20L * 1024 * 1024;
    const int RawRetentionDays = 7;

    /// <summary>docs/statistics.md decision 1 / owner decision: down from 6 months. The long run moved to cycles.csv/hourly-YYYY.csv, which are retained forever, so nothing is lost for statistics -- but replaying a window-shape row older than this becomes impossible (warm start only ever looks back one month anyway).</summary>
    public const int CsvRetentionDays = 60;

    static readonly string[] WindowShapeCsvHeader =
    {
        "utc_iso", "mono_ms", "five_hour_pct", "five_hour_resets_at_raw",
        "seven_day_pct", "seven_day_resets_at_raw", "poll_latency_ms", "source",
    };

    readonly string _logDir;
    readonly Channel<RawEntry> _rawChannel = Channel.CreateBounded<RawEntry>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    readonly Channel<CsvEntry> _csvChannel = Channel.CreateBounded<CsvEntry>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    // docs/statistics.md: cycles.csv/hourly-YYYY.csv rows are rare (a window closes at most a
    // couple of times a day) but just as archival as window-shape.csv's own rows -- DropWrite,
    // never DropOldest, for the same reason the CSV channel above is.
    readonly Channel<CycleArchiveCsv.Row> _cycleChannel = Channel.CreateBounded<CycleArchiveCsv.Row>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    readonly Channel<HourlyRollupCsv.Row> _hourlyChannel = Channel.CreateBounded<HourlyRollupCsv.Row>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    readonly CancellationTokenSource _cts = new();
    readonly Task _consumerTask;

    long _csvDropCount;
    string? _rawDatePart;
    int _rawSegment;
    string? _rawPath;
    bool _retentionSwept;

    public DiskLogSink(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
        _consumerTask = Task.Factory.StartNew(
            RunAsync, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Non-blocking: DropOldest bounded-channel writes never wait.</summary>
    public void EnqueueRaw(string channel, string line)
    {
        string entry = $"{DateTimeOffset.UtcNow:O} [{channel}] {Redact(line)}{Environment.NewLine}";
        _rawChannel.Writer.TryWrite(new RawEntry(DateTimeOffset.UtcNow, entry));
    }

    /// <summary>Non-blocking. Counts (and logs) a drop rather than ever losing a row silently.</summary>
    public void EnqueueCsv(UsageSnapshot snapshot, TimeSpan latency)
    {
        var entry = new CsvEntry(DateTimeOffset.UtcNow, snapshot, latency);
        if (!_csvChannel.Writer.TryWrite(entry))
        {
            long total = Interlocked.Increment(ref _csvDropCount);
            EnqueueRaw("fault", $"window-shape.csv row dropped (CSV queue full); total drops so far={total}");
        }
    }

    /// <summary>docs/statistics.md: one closed window. Non-blocking, same drop-and-log contract as EnqueueCsv.</summary>
    public void EnqueueCycle(CycleArchiveCsv.Row row)
    {
        if (!_cycleChannel.Writer.TryWrite(row))
            EnqueueRaw("fault", "cycles.csv row dropped (cycle queue full)");
    }

    /// <summary>docs/statistics.md: one closed clock hour. Non-blocking, same drop-and-log contract as EnqueueCsv.</summary>
    public void EnqueueHourly(HourlyRollupCsv.Row row)
    {
        if (!_hourlyChannel.Writer.TryWrite(row))
            EnqueueRaw("fault", "hourly-*.csv row dropped (hourly queue full)");
    }

    async Task RunAsync()
    {
        ChannelReader<RawEntry> rawReader = _rawChannel.Reader;
        ChannelReader<CsvEntry> csvReader = _csvChannel.Reader;
        ChannelReader<CycleArchiveCsv.Row> cycleReader = _cycleChannel.Reader;
        ChannelReader<HourlyRollupCsv.Row> hourlyReader = _hourlyChannel.Reader;

        while (true)
        {
            if (DrainOnce(rawReader, csvReader, cycleReader, hourlyReader)) continue;
            if (rawReader.Completion.IsCompleted && csvReader.Completion.IsCompleted
                && cycleReader.Completion.IsCompleted && hourlyReader.Completion.IsCompleted) break;

            try
            {
                Task rawWait = rawReader.WaitToReadAsync(_cts.Token).AsTask();
                Task csvWait = csvReader.WaitToReadAsync(_cts.Token).AsTask();
                Task cycleWait = cycleReader.WaitToReadAsync(_cts.Token).AsTask();
                Task hourlyWait = hourlyReader.WaitToReadAsync(_cts.Token).AsTask();
                await Task.WhenAny(rawWait, csvWait, cycleWait, hourlyWait).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DrainOnce(rawReader, csvReader, cycleReader, hourlyReader); // best-effort final flush; caller bounds total wait via DisposeAsync's deadline
    }

    bool DrainOnce(ChannelReader<RawEntry> rawReader, ChannelReader<CsvEntry> csvReader,
        ChannelReader<CycleArchiveCsv.Row> cycleReader, ChannelReader<HourlyRollupCsv.Row> hourlyReader)
    {
        bool did = false;
        while (cycleReader.TryRead(out CycleArchiveCsv.Row cycle)) { WriteCycle(cycle); did = true; }
        while (hourlyReader.TryRead(out HourlyRollupCsv.Row hourly)) { WriteHourly(hourly); did = true; }
        while (rawReader.TryRead(out RawEntry raw)) { WriteRaw(raw); did = true; }
        while (csvReader.TryRead(out CsvEntry csv)) { WriteCsv(csv); did = true; }
        return did;
    }

    void WriteRaw(RawEntry entry)
    {
        try
        {
            SweepRetentionOnce();
            string path = CurrentRawPath(entry.At);
            File.AppendAllText(path, entry.Text, Encoding.UTF8);
        }
        catch
        {
            // logging must never take the app down, and there is nowhere left to report this.
        }
    }

    string CurrentRawPath(DateTimeOffset at)
    {
        string datePart = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_rawDatePart != datePart)
        {
            _rawDatePart = datePart;
            _rawSegment = 0;
            _rawPath = Path.Combine(_logDir, $"{datePart}.log");
        }
        else if (_rawPath != null && SafeLength(_rawPath) > RawFileCapBytes)
        {
            _rawSegment++;
            _rawPath = Path.Combine(_logDir, $"{datePart}.{_rawSegment}.log");
        }
        return _rawPath!;
    }

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    void SweepRetentionOnce()
    {
        if (_retentionSwept) return;
        _retentionSwept = true;
        try
        {
            DateTime rawCutoff = DateTime.UtcNow.AddDays(-RawRetentionDays);
            foreach (string file in Directory.EnumerateFiles(_logDir, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < rawCutoff) File.Delete(file);
            }

            DateTime csvCutoff = DateTime.UtcNow.AddDays(-CsvRetentionDays);
            foreach (string file in Directory.EnumerateFiles(_logDir, "window-shape-*.csv"))
            {
                if (File.GetLastWriteTimeUtc(file) < csvCutoff) File.Delete(file);
            }
        }
        catch
        {
            // retention is best-effort; a sweep failure must never block logging.
        }
    }

    void WriteCsv(CsvEntry entry)
    {
        try
        {
            string path = MonthlyCsvPath(_logDir, entry.At);
            string[] row =
            {
                entry.At.ToString("O", CultureInfo.InvariantCulture),
                Environment.TickCount64.ToString(CultureInfo.InvariantCulture),
                entry.Snapshot.SessionUtilization.ToString(CultureInfo.InvariantCulture),
                CsvField(entry.Snapshot.SessionResetsAt),
                entry.Snapshot.WeeklyUtilization?.ToString(CultureInfo.InvariantCulture) ?? "",
                CsvField(entry.Snapshot.WeeklyResetsAt),
                entry.Latency.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                "get_usage",
            };

            bool exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!exists) writer.WriteLine(string.Join(",", WindowShapeCsvHeader));
            writer.WriteLine(string.Join(",", row));
            writer.Flush(); // must survive a crash or a kill, not just a clean exit
        }
        catch
        {
            // a measurement-logging failure must never take the app down.
        }
    }

    /// <summary>Shared with CsvReplay.ReadRowsForWarmStart so writer and reader can never disagree on the naming scheme.</summary>
    public static string MonthlyCsvPath(string logDir, DateTimeOffset at) =>
        Path.Combine(logDir, $"window-shape-{at:yyyy-MM}.csv");

    void WriteCycle(CycleArchiveCsv.Row row)
    {
        try { CycleArchiveCsv.AppendRow(Path.Combine(_logDir, CycleArchiveCsv.FileName), row); }
        catch { /* a measurement-logging failure must never take the app down. */ }
    }

    void WriteHourly(HourlyRollupCsv.Row row)
    {
        try { HourlyRollupCsv.AppendRow(_logDir, row); }
        catch { /* a measurement-logging failure must never take the app down. */ }
    }

    static string CsvField(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + raw.Replace("\"", "\"\"") + "\""
            : raw;
    }

    /// <summary>Defensive only: never let token or credential material reach the rolling log.</summary>
    static string Redact(string line)
    {
        if (line.Contains("sk-ant-", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"access_token\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"refresh_token\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Bearer ", StringComparison.Ordinal))
        {
            return "[redacted: possible credential material]";
        }
        return line;
    }

    /// <summary>Idempotent, bounded shutdown: stop accepting new work, let the consumer drain what is already queued up to `deadline`, then give up -- this must never block the shutdown path indefinitely.</summary>
    public async ValueTask DisposeAsync(TimeSpan deadline)
    {
        _rawChannel.Writer.TryComplete();
        _csvChannel.Writer.TryComplete();
        _cycleChannel.Writer.TryComplete();
        _hourlyChannel.Writer.TryComplete();
        try
        {
            await Task.WhenAny(_consumerTask, Task.Delay(deadline)).ConfigureAwait(false);
        }
        catch
        {
            // best-effort flush only.
        }
        finally
        {
            _cts.Cancel();
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(TimeSpan.FromSeconds(2));

    readonly record struct RawEntry(DateTimeOffset At, string Text);
    readonly record struct CsvEntry(DateTimeOffset At, UsageSnapshot Snapshot, TimeSpan Latency);
}

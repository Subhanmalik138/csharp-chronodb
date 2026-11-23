namespace ChronoDB.Storage;

/// <summary>
/// The database: an in-memory index of <see cref="TimeSeries"/> backed by a
/// write-ahead log and Gorilla-compressed immutable segments.
///
/// Durability model: every append hits the WAL (flushed) before it is visible
/// in memory. On open, segments load first, then the WAL replays on top.
/// A flush snapshots the whole in-memory state into one new segment and
/// truncates the WAL, which doubles as compaction — pruned samples never
/// make it into the new segment.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly string _directory;
    private readonly string _walPath;
    private readonly string _segmentsDir;
    private readonly DatabaseOptions _options;
    private readonly Dictionary<string, TimeSeries> _series = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private Wal? _wal;
    private bool _disposed;

    private Database(string directory, DatabaseOptions options)
    {
        _directory = directory;
        _options = options;
        _walPath = Path.Combine(directory, "wal.log");
        _segmentsDir = Path.Combine(directory, "segments");
    }

    public static Database Open(string directory, DatabaseOptions? options = null)
    {
        var opts = options ?? new DatabaseOptions();
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "segments"));

        var db = new Database(directory, opts);

        // Segments first (oldest first), then the WAL on top.
        foreach (var file in ListSegments(db._segmentsDir))
            foreach (var (key, samples) in Segment.Read(file))
                db.GetOrCreate(key).AppendRange(samples);

        foreach (var (key, sample) in Wal.Replay(db._walPath))
            db.GetOrCreate(key).Append(sample);

        db._wal = new Wal(db._walPath);

        // Retention applies on open, and the flush rewrites segments without
        // the pruned samples so they cannot come back on the next open.
        if (opts.Retention is not null)
        {
            var cutoff = DateTimeOffset.UtcNow.Add(-opts.Retention.Value).ToUnixTimeMilliseconds();
            db.PruneOlderThan(cutoff);
            db.Flush();
        }

        return db;
    }

    /// <summary>Append one sample. Durable (WAL-flushed) before it returns.</summary>
    public void Append(SeriesKey key, Sample sample)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _wal!.Append(key, sample);
            GetOrCreate(key).Append(sample);
            if (_wal.Length >= _options.MaxWalBytesBeforeFlush)
                FlushLocked();
        }
    }

    public void AppendRange(SeriesKey key, IEnumerable<Sample> samples)
    {
        foreach (var sample in samples)
            Append(key, sample);
    }

    public IReadOnlyList<SeriesKey> SeriesKeys
    {
        get
        {
            lock (_lock)
                return _series.Values
                    .Select(s => s.Key)
                    .OrderBy(k => k.ToString(), StringComparer.Ordinal)
                    .ToList();
        }
    }

    public TimeSeries? GetSeries(SeriesKey key)
    {
        lock (_lock)
            return _series.TryGetValue(key.ToString(), out var series) ? series : null;
    }

    public IReadOnlyList<Sample> Query(SeriesKey key, long fromMs, long toMs) =>
        GetSeries(key)?.Range(fromMs, toMs) ?? Array.Empty<Sample>();

    /// <summary>Write the current state to a new segment and truncate the WAL.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        var snapshot = _series.Values
            .Where(s => s.Count > 0)
            .ToDictionary(s => s.Key, s => (IReadOnlyList<Sample>)s.Snapshot());

        string? newSegment = null;
        if (snapshot.Count > 0)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            newSegment = $"seg-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{suffix}.seg";
            Segment.Write(Path.Combine(_segmentsDir, newSegment), snapshot);
        }

        _wal!.Dispose();
        File.WriteAllBytes(_walPath, Array.Empty<byte>());
        _wal = new Wal(_walPath);

        // The new segment holds the whole state; older segments are redundant.
        foreach (var file in ListSegments(_segmentsDir))
            if (Path.GetFileName(file) != newSegment)
                File.Delete(file);
    }

    /// <summary>Drop samples older than <paramref name="beforeMs"/> from memory. Returns the count removed.</summary>
    public long PruneOlderThan(long beforeMs)
    {
        lock (_lock)
        {
            long removed = 0;
            foreach (var series in _series.Values)
                removed += series.PruneOlderThan(beforeMs);
            return removed;
        }
    }

    public DatabaseStats Stats
    {
        get
        {
            lock (_lock)
            {
                var files = ListSegments(_segmentsDir);
                return new DatabaseStats(
                    _series.Count,
                    _series.Values.Sum(s => (long)s.Count),
                    _wal?.Length ?? 0,
                    files.Sum(f => new FileInfo(f).Length),
                    files.Count);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _wal?.Dispose();
            _wal = null;
        }
    }

    private static List<string> ListSegments(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "seg-*.seg").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

    private TimeSeries GetOrCreate(SeriesKey key)
    {
        if (!_series.TryGetValue(key.ToString(), out var series))
        {
            series = new TimeSeries(key);
            _series[key.ToString()] = series;
        }
        return series;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Database));
    }
}

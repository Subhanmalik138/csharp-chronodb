namespace ChronoDB;

/// <summary>
/// An in-memory, append-only series of samples, kept sorted by timestamp.
/// Out-of-order writes are inserted at the right position (binary search),
/// which is what makes late-arriving samples and WAL replay safe.
/// All public members are thread-safe.
/// </summary>
public sealed class TimeSeries
{
    private readonly List<Sample> _samples = new();
    private readonly object _lock = new();

    public SeriesKey Key { get; }

    public TimeSeries(SeriesKey key) => Key = key;

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    public Sample? Latest
    {
        get { lock (_lock) return _samples.Count == 0 ? null : _samples[^1]; }
    }

    public Sample? Earliest
    {
        get { lock (_lock) return _samples.Count == 0 ? null : _samples[0]; }
    }

    /// <summary>
    /// Append a sample. A sample with a timestamp that already exists replaces
    /// the old value (last-write-wins), which also makes WAL replay idempotent.
    /// </summary>
    public void Append(Sample sample)
    {
        lock (_lock)
        {
            if (_samples.Count == 0)
            {
                _samples.Add(sample);
                return;
            }
            var last = _samples[^1];
            if (sample.TimestampUnixMs > last.TimestampUnixMs)
            {
                _samples.Add(sample);
                return;
            }
            if (sample.TimestampUnixMs == last.TimestampUnixMs)
            {
                _samples[^1] = sample;
                return;
            }
            var index = _samples.BinarySearch(sample, SampleTimestampComparer.Instance);
            if (index >= 0)
                _samples[index] = sample;
            else
                _samples.Insert(~index, sample);
        }
    }

    public void AppendRange(IEnumerable<Sample> samples)
    {
        foreach (var s in samples) Append(s);
    }

    /// <summary>Samples with <paramref name="fromMs"/> &lt;= t &lt;= <paramref name="toMs"/>.</summary>
    public IReadOnlyList<Sample> Range(long fromMs, long toMs)
    {
        lock (_lock)
        {
            if (_samples.Count == 0) return Array.Empty<Sample>();
            var lo = LowerBound(fromMs);
            var hi = UpperBound(toMs);
            if (lo >= hi) return Array.Empty<Sample>();
            return _samples.GetRange(lo, hi - lo);
        }
    }

    public IReadOnlyList<Sample> Snapshot()
    {
        lock (_lock) return _samples.ToArray();
    }

    /// <summary>Remove all samples older than <paramref name="beforeMs"/> (retention).</summary>
    public int PruneOlderThan(long beforeMs)
    {
        lock (_lock)
        {
            var cut = LowerBound(beforeMs);
            if (cut == 0) return 0;
            _samples.RemoveRange(0, cut);
            return cut;
        }
    }

    private int LowerBound(long t)
    {
        var lo = 0; var hi = _samples.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_samples[mid].TimestampUnixMs < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private int UpperBound(long t)
    {
        var lo = 0; var hi = _samples.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_samples[mid].TimestampUnixMs <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private sealed class SampleTimestampComparer : IComparer<Sample>
    {
        public static readonly SampleTimestampComparer Instance = new();
        public int Compare(Sample x, Sample y) => x.TimestampUnixMs.CompareTo(y.TimestampUnixMs);
    }
}

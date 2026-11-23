using ChronoDB.Storage;

namespace ChronoDB.Query;

/// <summary>How samples inside one downsampling bucket are combined.</summary>
public enum Aggregation
{
    Avg,
    Min,
    Max,
    Sum,
    Count,
    /// <summary>Per-second rate of increase (for counters). Needs ≥ 2 samples per bucket.</summary>
    Rate,
}

/// <summary>One downsampled point: the bucket start and the aggregated value.</summary>
public readonly record struct QueryPoint(long TimestampUnixMs, double Value);

/// <summary>All points of one series for a range query.</summary>
public sealed record SeriesResult(SeriesKey Key, IReadOnlyList<QueryPoint> Points);

/// <summary>
/// Query engine over a <see cref="Database"/>: instant queries (latest value
/// per matching series) and range queries with step downsampling.
///
/// A range query splits [from, to] into step-sized buckets aligned to `from`
/// and aggregates each bucket. A bucket with no samples produces no point,
/// so sparse series stay sparse instead of inventing zeros.
/// </summary>
public sealed class QueryEngine
{
    private readonly Database _database;

    public QueryEngine(Database database) => _database = database;

    /// <summary>Latest value of every series matching the selector (at or before <paramref name="atMs"/>).</summary>
    public IReadOnlyList<(SeriesKey Key, Sample Sample)> InstantQuery(Selector selector, long? atMs = null)
    {
        var results = new List<(SeriesKey, Sample)>();
        foreach (var key in _database.SeriesKeys)
        {
            if (!selector.Matches(key)) continue;
            var series = _database.GetSeries(key);
            if (series is null) continue;
            var latest = atMs is null
                ? series.Latest
                : series.Range(long.MinValue, atMs.Value).LastOrDefault();
            if (latest is not null)
                results.Add((key, latest.Value));
        }
        return results;
    }

    /// <summary>
    /// Range query: downsample every matching series into <paramref name="stepMs"/> buckets.
    /// </summary>
    public IReadOnlyList<SeriesResult> RangeQuery(
        Selector selector, long fromMs, long toMs, long stepMs, Aggregation aggregation)
    {
        if (fromMs > toMs)
            throw new ArgumentException("from must be <= to.", nameof(fromMs));
        if (stepMs <= 0)
            throw new ArgumentException("step must be positive.", nameof(stepMs));

        var results = new List<SeriesResult>();
        foreach (var key in _database.SeriesKeys)
        {
            if (!selector.Matches(key)) continue;
            var series = _database.GetSeries(key);
            if (series is null) continue;
            var points = Aggregate(series.Range(fromMs, toMs), fromMs, stepMs, aggregation);
            results.Add(new SeriesResult(key, points));
        }
        return results;
    }

    internal static IReadOnlyList<QueryPoint> Aggregate(
        IReadOnlyList<Sample> samples, long fromMs, long stepMs, Aggregation aggregation)
    {
        if (samples.Count == 0)
            return Array.Empty<QueryPoint>();

        // Bucket index -> samples in that bucket. Samples arrive sorted, so a
        // single pass with a running bucket is enough.
        var points = new List<QueryPoint>();
        var bucketStart = fromMs;
        var bucket = new List<Sample>();
        void FlushBucket()
        {
            if (bucket.Count == 0) return;
            var value = AggregateBucket(bucket, aggregation);
            if (value is not null)
                points.Add(new QueryPoint(bucketStart, value.Value));
            bucket.Clear();
        }

        foreach (var sample in samples)
        {
            while (sample.TimestampUnixMs >= bucketStart + stepMs)
            {
                FlushBucket();
                bucketStart += stepMs;
            }
            bucket.Add(sample);
        }
        FlushBucket();
        return points;
    }

    private static double? AggregateBucket(List<Sample> bucket, Aggregation aggregation)
    {
        switch (aggregation)
        {
            case Aggregation.Avg:
                return bucket.Average(s => s.Value);
            case Aggregation.Min:
                return bucket.Min(s => s.Value);
            case Aggregation.Max:
                return bucket.Max(s => s.Value);
            case Aggregation.Sum:
                return bucket.Sum(s => s.Value);
            case Aggregation.Count:
                return bucket.Count;
            case Aggregation.Rate:
            {
                if (bucket.Count < 2) return null;
                var first = bucket[0];
                var last = bucket[^1];
                var seconds = (last.TimestampUnixMs - first.TimestampUnixMs) / 1000.0;
                if (seconds <= 0) return null;
                // Counters only move forward; a decrease means a reset — use
                // the increase since the reset instead of going negative.
                var increase = last.Value >= first.Value
                    ? last.Value - first.Value
                    : last.Value;
                return increase / seconds;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, null);
        }
    }

    public static Aggregation ParseAggregation(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "avg" or "average" => Aggregation.Avg,
            "min" => Aggregation.Min,
            "max" => Aggregation.Max,
            "sum" => Aggregation.Sum,
            "count" => Aggregation.Count,
            "rate" => Aggregation.Rate,
            _ => throw new FormatException(
                $"Unknown aggregation '{name}'. Expected one of: avg, min, max, sum, count, rate."),
        };
}

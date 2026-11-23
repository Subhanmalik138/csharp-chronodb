namespace ChronoDB;

/// <summary>
/// A single observation: a value recorded at a point in time.
/// Timestamps are Unix milliseconds (UTC); values are doubles, which cover
/// gauges, counters and histograms' sums alike.
/// </summary>
public readonly record struct Sample(long TimestampUnixMs, double Value)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUnixMs);
}

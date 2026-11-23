namespace ChronoDB.Storage;

/// <summary>Tuning knobs for <see cref="Database"/>.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Flush the WAL into a compressed segment once it reaches this size. Default 1 MiB.</summary>
    public long MaxWalBytesBeforeFlush { get; set; } = 1_048_576;

    /// <summary>When set, samples older than this are dropped on open and on flush.</summary>
    public TimeSpan? Retention { get; set; }
}

/// <summary>Point-in-time storage statistics.</summary>
public sealed record DatabaseStats(
    int SeriesCount,
    long TotalSamples,
    long WalBytes,
    long SegmentBytes,
    int SegmentCount);

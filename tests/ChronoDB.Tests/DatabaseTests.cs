using Xunit;
using ChronoDB.Storage;

namespace ChronoDB.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronodb-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static SeriesKey Cpu(string host) => new("cpu_usage", new Dictionary<string, string> { ["host"] = host });

    [Fact]
    public void AppendAndQuery()
    {
        using var db = Database.Open(_dir);
        db.Append(Cpu("a"), new Sample(1000, 0.5));
        db.Append(Cpu("a"), new Sample(2000, 0.7));
        var samples = db.Query(Cpu("a"), 0, 3000);
        Assert.Equal(2, samples.Count);
        Assert.Equal(0.7, samples[1].Value);
    }

    [Fact]
    public void ReopenReplaysWal()
    {
        using (var db = Database.Open(_dir))
        {
            db.Append(Cpu("a"), new Sample(1000, 1));
            db.Append(Cpu("a"), new Sample(2000, 2));
        }
        using var reopened = Database.Open(_dir);
        Assert.Equal(2, reopened.Query(Cpu("a"), 0, 3000).Count);
        Assert.Equal(2, reopened.GetSeries(Cpu("a"))!.Latest!.Value.Value);
    }

    [Fact]
    public void FlushCreatesSegmentAndReopenLoadsIt()
    {
        using (var db = Database.Open(_dir))
        {
            for (var i = 0; i < 50; i++)
                db.Append(Cpu("a"), new Sample(1000 + i * 1000, i));
            db.Flush();
            Assert.Equal(1, db.Stats.SegmentCount);
            Assert.Equal(0, db.Stats.WalBytes);
        }
        using var reopened = Database.Open(_dir);
        var samples = reopened.Query(Cpu("a"), 0, long.MaxValue);
        Assert.Equal(50, samples.Count);
        Assert.Equal(49, samples[^1].Value);
    }

    [Fact]
    public void WalAppendsOnTopOfSegmentsAfterReopen()
    {
        using (var db = Database.Open(_dir))
        {
            db.Append(Cpu("a"), new Sample(1000, 1));
            db.Flush();
        }
        using (var db = Database.Open(_dir))
        {
            db.Append(Cpu("a"), new Sample(2000, 2));
        }
        using var reopened = Database.Open(_dir);
        Assert.Equal(2, reopened.Query(Cpu("a"), 0, 3000).Count);
    }

    [Fact]
    public void TornWalTailIsIgnoredOnReopen()
    {
        using (var db = Database.Open(_dir))
        {
            db.Append(Cpu("a"), new Sample(1000, 1));
            db.Append(Cpu("a"), new Sample(2000, 2));
        }
        File.AppendAllText(Path.Combine(_dir, "wal.log"), "garbage\x00\x01\x02truncated-record");
        using var reopened = Database.Open(_dir);
        Assert.Equal(2, reopened.Query(Cpu("a"), 0, 3000).Count);
    }

    [Fact]
    public void AutoFlushOnWalSize()
    {
        using var db = Database.Open(_dir, new DatabaseOptions { MaxWalBytesBeforeFlush = 200 });
        for (var i = 0; i < 20; i++)
            db.Append(Cpu("a"), new Sample(1000 + i * 1000, i));
        Assert.Equal(1, db.Stats.SegmentCount);
        Assert.Equal(20, db.Query(Cpu("a"), 0, long.MaxValue).Count);
    }

    [Fact]
    public void RetentionPrunesOnOpen()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var db = Database.Open(_dir))
        {
            db.Append(Cpu("a"), new Sample(now - TimeSpan.FromDays(10).Milliseconds, 1));
            db.Append(Cpu("a"), new Sample(now, 2));
            db.Flush();
        }
        using var reopened = Database.Open(_dir, new DatabaseOptions { Retention = TimeSpan.FromDays(7) });
        var samples = reopened.Query(Cpu("a"), 0, long.MaxValue);
        Assert.Single(samples);
        Assert.Equal(2, samples[0].Value);
        // And the pruned sample must not come back after another reopen.
        using var again = Database.Open(_dir);
        Assert.Single(again.Query(Cpu("a"), 0, long.MaxValue));
    }

    [Fact]
    public void DuplicateTimestampAcrossReopenKeepsLatestValue()
    {
        using (var db = Database.Open(_dir))
            db.Append(Cpu("a"), new Sample(1000, 1));
        using (var db = Database.Open(_dir))
            db.Append(Cpu("a"), new Sample(1000, 2));
        using var reopened = Database.Open(_dir);
        var samples = reopened.Query(Cpu("a"), 0, 2000);
        Assert.Single(samples);
        Assert.Equal(2, samples[0].Value);
    }

    [Fact]
    public void StatsReflectState()
    {
        using var db = Database.Open(_dir);
        db.Append(Cpu("a"), new Sample(1000, 1));
        db.Append(Cpu("b"), new Sample(1000, 2));
        var stats = db.Stats;
        Assert.Equal(2, stats.SeriesCount);
        Assert.Equal(2, stats.TotalSamples);
        Assert.True(stats.WalBytes > 0);
        Assert.Equal(0, stats.SegmentCount);
    }

    [Fact]
    public void SeriesKeysAreSorted()
    {
        using var db = Database.Open(_dir);
        db.Append(Cpu("b"), new Sample(1000, 1));
        db.Append(Cpu("a"), new Sample(1000, 1));
        db.Append(new SeriesKey("mem"), new Sample(1000, 1));
        var keys = db.SeriesKeys.Select(k => k.ToString()).ToArray();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), keys);
    }
}

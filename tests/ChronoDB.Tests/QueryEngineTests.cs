using Xunit;
using ChronoDB.Query;
using ChronoDB.Storage;

namespace ChronoDB.Tests;

public sealed class QueryEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronodb-qtests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static SeriesKey Key(string metric, params (string, string)[] labels) =>
        new(metric, labels.ToDictionary(l => l.Item1, l => l.Item2));

    private QueryEngine EngineWith(params (SeriesKey Key, Sample[] Samples)[] data)
    {
        var db = Database.Open(_dir);
        foreach (var (key, samples) in data)
            db.AppendRange(key, samples);
        return new QueryEngine(db);
    }

    [Fact]
    public void SelectorMatchesExactLabels()
    {
        var selector = Selector.Parse("http_requests_total{method=\"GET\", status=\"200\"}");
        Assert.True(selector.Matches(Key("http_requests_total", ("method", "GET"), ("status", "200"))));
        Assert.False(selector.Matches(Key("http_requests_total", ("method", "POST"), ("status", "200"))));
        Assert.False(selector.Matches(Key("http_requests_total", ("method", "GET"))));
        Assert.False(selector.Matches(Key("other_metric", ("method", "GET"), ("status", "200"))));
    }

    [Fact]
    public void SelectorNotEqualMatcher()
    {
        var selector = Selector.Parse("cpu{host!=\"web-1\"}");
        Assert.True(selector.Matches(Key("cpu", ("host", "web-2"))));
        Assert.False(selector.Matches(Key("cpu", ("host", "web-1"))));
        Assert.True(selector.Matches(Key("cpu"))); // missing label != value
    }

    [Fact]
    public void SelectorRegexMatchers()
    {
        var selector = Selector.Parse("cpu{host=~\"web-.*\"}");
        Assert.True(selector.Matches(Key("cpu", ("host", "web-3"))));
        Assert.False(selector.Matches(Key("cpu", ("host", "db-1"))));

        var negated = Selector.Parse("cpu{host!~\"web-.*\"}");
        Assert.True(negated.Matches(Key("cpu", ("host", "db-1"))));
        Assert.False(negated.Matches(Key("cpu", ("host", "web-3"))));
    }

    [Fact]
    public void SelectorWithoutMetricMatchesAllMetrics()
    {
        var selector = Selector.Parse("{env=\"prod\"}");
        Assert.True(selector.Matches(Key("cpu", ("env", "prod"))));
        Assert.True(selector.Matches(Key("mem", ("env", "prod"))));
        Assert.False(selector.Matches(Key("cpu", ("env", "dev"))));
    }

    [Fact]
    public void SelectorParseRejectsMalformed()
    {
        Assert.Throws<FormatException>(() => Selector.Parse("cpu{host=\"a\""));
        Assert.Throws<FormatException>(() => Selector.Parse("cpu{host=a}"));
        Assert.Throws<FormatException>(() => Selector.Parse(""));
    }

    [Fact]
    public void RangeQueryAvgDownsamplesIntoBuckets()
    {
        var engine = EngineWith((Key("cpu"), Enumerable.Range(0, 12)
            .Select(i => new Sample(i * 10_000, i + 1)).ToArray()));
        var results = engine.RangeQuery(Selector.Parse("cpu"), 0, 120_000, 60_000, Aggregation.Avg);
        Assert.Single(results);
        // Buckets [0,60s): values 1..6 avg 3.5; [60s,120s): values 7..12 avg 9.5.
        Assert.Equal(2, results[0].Points.Count);
        Assert.Equal(0, results[0].Points[0].TimestampUnixMs);
        Assert.Equal(3.5, results[0].Points[0].Value);
        Assert.Equal(60_000, results[0].Points[1].TimestampUnixMs);
        Assert.Equal(9.5, results[0].Points[1].Value);
    }

    [Fact]
    public void RangeQuerySumMinMaxCount()
    {
        var samples = new[] { new Sample(0, 2.0), new Sample(1000, 5.0), new Sample(2000, 3.0) };
        var engine = EngineWith((Key("v"), samples));
        Assert.Equal(10, engine.RangeQuery(Selector.Parse("v"), 0, 3000, 60_000, Aggregation.Sum)[0].Points[0].Value);
        Assert.Equal(2, engine.RangeQuery(Selector.Parse("v"), 0, 3000, 60_000, Aggregation.Min)[0].Points[0].Value);
        Assert.Equal(5, engine.RangeQuery(Selector.Parse("v"), 0, 3000, 60_000, Aggregation.Max)[0].Points[0].Value);
        Assert.Equal(3, engine.RangeQuery(Selector.Parse("v"), 0, 3000, 60_000, Aggregation.Count)[0].Points[0].Value);
    }

    [Fact]
    public void RangeQueryRateForCounters()
    {
        // Counter rising 10/second.
        var samples = Enumerable.Range(0, 7)
            .Select(i => new Sample(i * 1000, i * 10.0)).ToArray();
        var engine = EngineWith((Key("req"), samples));
        var results = engine.RangeQuery(Selector.Parse("req"), 0, 7000, 60_000, Aggregation.Rate);
        Assert.Single(results[0].Points);
        Assert.Equal(10, results[0].Points[0].Value, precision: 9);
    }

    [Fact]
    public void RangeQueryRateSkipsSingleSampleBuckets()
    {
        var samples = new[] { new Sample(0, 5.0) };
        var engine = EngineWith((Key("req"), samples));
        var results = engine.RangeQuery(Selector.Parse("req"), 0, 60_000, 60_000, Aggregation.Rate);
        Assert.Empty(results[0].Points);
    }

    [Fact]
    public void RangeQuerySkipsEmptyBuckets()
    {
        var samples = new[] { new Sample(0, 1.0), new Sample(180_000, 2.0) };
        var engine = EngineWith((Key("v"), samples));
        var results = engine.RangeQuery(Selector.Parse("v"), 0, 240_000, 60_000, Aggregation.Avg);
        // Points only in buckets 0 and 3; buckets 1 and 2 stay absent.
        Assert.Equal(new[] { 0L, 180_000L }, results[0].Points.Select(p => p.TimestampUnixMs).ToArray());
    }

    [Fact]
    public void RangeQueryReturnsNoSeriesWhenNothingMatches()
    {
        var engine = EngineWith((Key("cpu"), new[] { new Sample(0, 1.0) }));
        Assert.Empty(engine.RangeQuery(Selector.Parse("mem"), 0, 1000, 60_000, Aggregation.Avg));
    }

    [Fact]
    public void InstantQueryReturnsLatestPerSeries()
    {
        var engine = EngineWith(
            (Key("cpu", ("host", "a")), new[] { new Sample(1000, 1.0), new Sample(2000, 2.0) }),
            (Key("cpu", ("host", "b")), new[] { new Sample(1500, 5.0) }));
        var results = engine.InstantQuery(Selector.Parse("cpu"));
        Assert.Equal(2, results.Count);
        Assert.Equal(2.0, results.Single(r => r.Key.Labels["host"] == "a").Sample.Value);
        Assert.Equal(5.0, results.Single(r => r.Key.Labels["host"] == "b").Sample.Value);
    }

    [Fact]
    public void InstantQueryAtTimeRespectsCutoff()
    {
        var engine = EngineWith(
            (Key("cpu"), new[] { new Sample(1000, 1.0), new Sample(2000, 2.0), new Sample(3000, 3.0) }));
        var results = engine.InstantQuery(Selector.Parse("cpu"), atMs: 2000);
        Assert.Single(results);
        Assert.Equal(2.0, results[0].Sample.Value);
    }

    [Fact]
    public void ParseAggregationRejectsUnknown()
    {
        Assert.Throws<FormatException>(() => QueryEngine.ParseAggregation("median"));
        Assert.Equal(Aggregation.Avg, QueryEngine.ParseAggregation("average"));
    }
}

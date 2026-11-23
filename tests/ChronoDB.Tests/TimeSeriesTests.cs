using Xunit;
namespace ChronoDB.Tests;

public sealed class TimeSeriesTests
{
    private static SeriesKey Key => new("cpu");

    [Fact]
    public void AppendInOrderKeepsOrder()
    {
        var series = new TimeSeries(Key);
        series.Append(new Sample(1000, 1));
        series.Append(new Sample(2000, 2));
        series.Append(new Sample(3000, 3));
        Assert.Equal(3, series.Count);
        Assert.Equal(new Sample(3000, 3), series.Latest);
        Assert.Equal(new Sample(1000, 1), series.Earliest);
    }

    [Fact]
    public void AppendOutOfOrderInsertsSorted()
    {
        var series = new TimeSeries(Key);
        series.Append(new Sample(3000, 3));
        series.Append(new Sample(1000, 1));
        series.Append(new Sample(2000, 2));
        var snapshot = series.Snapshot();
        Assert.Equal(new[] { 1000L, 2000L, 3000L }, snapshot.Select(s => s.TimestampUnixMs).ToArray());
    }

    [Fact]
    public void DuplicateTimestampOverwritesValue()
    {
        var series = new TimeSeries(Key);
        series.Append(new Sample(1000, 1));
        series.Append(new Sample(1000, 2));
        Assert.Equal(1, series.Count);
        Assert.Equal(2, series.Latest!.Value.Value);
    }

    [Fact]
    public void RangeIsInclusiveOnBothEnds()
    {
        var series = new TimeSeries(Key);
        for (var i = 1; i <= 5; i++)
            series.Append(new Sample(i * 1000, i));
        var range = series.Range(2000, 4000);
        Assert.Equal(new[] { 2000L, 3000L, 4000L }, range.Select(s => s.TimestampUnixMs).ToArray());
    }

    [Fact]
    public void RangeEmptyWhenNoOverlap()
    {
        var series = new TimeSeries(Key);
        series.Append(new Sample(1000, 1));
        Assert.Empty(series.Range(2000, 3000));
        Assert.Empty(series.Range(0, 500));
    }

    [Fact]
    public void PruneOlderThanRemovesOnlyOldSamples()
    {
        var series = new TimeSeries(Key);
        for (var i = 1; i <= 5; i++)
            series.Append(new Sample(i * 1000, i));
        Assert.Equal(2, series.PruneOlderThan(3000));
        Assert.Equal(3, series.Count);
        Assert.Equal(3000, series.Earliest!.Value.TimestampUnixMs);
    }

    [Fact]
    public void EmptySeriesHasNoLatestOrEarliest()
    {
        var series = new TimeSeries(Key);
        Assert.Null(series.Latest);
        Assert.Null(series.Earliest);
        Assert.Equal(0, series.Count);
    }
}

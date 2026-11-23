using Xunit;
namespace ChronoDB.Tests;

public sealed class SeriesKeyTests
{
    [Fact]
    public void ParseWithoutLabels()
    {
        var key = SeriesKey.Parse("cpu_usage");
        Assert.Equal("cpu_usage", key.Metric);
        Assert.Empty(key.Labels);
        Assert.Equal("cpu_usage", key.ToString());
    }

    [Fact]
    public void ParseWithLabelsSortsCanonically()
    {
        var key = SeriesKey.Parse("http_requests_total{status=\"200\", method=\"GET\"}");
        Assert.Equal("http_requests_total{method=\"GET\", status=\"200\"}", key.ToString());
    }

    [Fact]
    public void ParseEscapedQuoteInLabel()
    {
        var key = SeriesKey.Parse("msg{text=\"say \\\"hi\\\"\"}");
        Assert.Equal("say \"hi\"", key.Labels["text"]);
        Assert.Equal("msg{text=\"say \\\"hi\\\"\"}", key.ToString());
    }

    [Fact]
    public void ParseRejectsMissingClosingBrace()
    {
        Assert.Throws<FormatException>(() => SeriesKey.Parse("cpu{host=\"a\""));
    }

    [Fact]
    public void ParseRejectsUnquotedLabelValue()
    {
        Assert.Throws<FormatException>(() => SeriesKey.Parse("cpu{host=a}"));
    }

    [Fact]
    public void ConstructorRejectsEmptyMetric()
    {
        Assert.Throws<ArgumentException>(() => new SeriesKey(""));
    }

    [Fact]
    public void ConstructorRejectsInvalidMetricCharacters()
    {
        Assert.Throws<ArgumentException>(() => new SeriesKey("cpu usage"));
    }

    [Fact]
    public void EqualityIgnoresLabelOrder()
    {
        var a = SeriesKey.Parse("cpu{host=\"a\", core=\"0\"}");
        var b = SeriesKey.Parse("cpu{core=\"0\", host=\"a\"}");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ToStringRoundTrips()
    {
        const string text = "disk_io{device=\"sda\", mode=\"read\"}";
        Assert.Equal(text, SeriesKey.Parse(text).ToString());
    }
}

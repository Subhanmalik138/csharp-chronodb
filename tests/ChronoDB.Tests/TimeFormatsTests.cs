using Xunit;
namespace ChronoDB.Tests;

public sealed class TimeFormatsTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("5m", 300_000)]
    [InlineData("2h", 7_200_000)]
    [InlineData("7d", 604_800_000)]
    [InlineData("1500", 1500)]
    public void ParseDurationMsHandlesUnits(string text, long expected)
    {
        Assert.Equal(expected, TimeFormats.ParseDurationMs(text));
    }

    [Fact]
    public void ParseDurationMsRejectsUnknown()
    {
        Assert.Throws<FormatException>(() => TimeFormats.ParseDurationMs("3w"));
        Assert.Throws<FormatException>(() => TimeFormats.ParseDurationMs("soon"));
    }

    [Fact]
    public void ParseTimeHandlesUnixMs()
    {
        Assert.Equal(1_795_442_400_000L, TimeFormats.ParseTime("1795442400000", 0));
    }

    [Fact]
    public void ParseTimeHandlesIso8601()
    {
        var parsed = TimeFormats.ParseTime("2026-11-23T14:00:00Z", 0);
        Assert.Equal(1_795_442_400_000L, parsed);
    }

    [Fact]
    public void ParseTimeHandlesNow()
    {
        var now = 1_795_442_400_000L;
        Assert.Equal(now, TimeFormats.ParseTime("now", now));
    }

    [Fact]
    public void ParseTimeHandlesRelativeOffset()
    {
        var now = 1_795_442_400_000L;
        Assert.Equal(now - 3_600_000, TimeFormats.ParseTime("-1h", now));
        Assert.Equal(now - 90_000, TimeFormats.ParseTime("-90s", now));
    }

    [Fact]
    public void ParseTimeRejectsGarbage()
    {
        Assert.Throws<FormatException>(() => TimeFormats.ParseTime("yesterday", 0));
    }

    [Fact]
    public void FormatUnixMsProducesUtcTimestamp()
    {
        Assert.Equal("2026-11-23 14:00:00Z", TimeFormats.FormatUnixMs(1_795_442_400_000L));
    }
}

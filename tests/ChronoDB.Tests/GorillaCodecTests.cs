using Xunit;
using ChronoDB.Encoding;

namespace ChronoDB.Tests;

public sealed class GorillaCodecTests
{
    [Fact]
    public void EmptyRoundTrip()
    {
        var encoded = GorillaCodec.Encode(Array.Empty<Sample>());
        Assert.Empty(encoded);
        Assert.Empty(GorillaCodec.Decode(encoded, 0));
    }

    [Fact]
    public void SingleSampleRoundTrip()
    {
        var samples = new[] { new Sample(1_763_985_600_000, 42.5) };
        var decoded = GorillaCodec.Decode(GorillaCodec.Encode(samples), 1);
        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void RegularIntervalRoundTripAndCompressesWell()
    {
        var samples = new List<Sample>();
        var start = 1_763_985_600_000L;
        for (var i = 0; i < 1000; i++)
            // Two-decimal gauge values, as a real metrics pipeline would emit.
            samples.Add(new Sample(start + i * 1000, Math.Round(50 + 10 * Math.Sin(i / 10.0), 2)));
        var encoded = GorillaCodec.Encode(samples);
        var decoded = GorillaCodec.Decode(encoded, samples.Count);
        Assert.Equal(samples, decoded);
        // Raw storage would be 16 bytes/sample; Gorilla should use well under half.
        Assert.True(encoded.Length < samples.Count * 8,
            $"Expected strong compression, got {encoded.Length} bytes for {samples.Count} samples.");
    }

    [Fact]
    public void IrregularTimestampsRoundTrip()
    {
        var random = new Random(7);
        var samples = new List<Sample>();
        var t = 1_763_985_600_000L;
        for (var i = 0; i < 500; i++)
        {
            t += random.Next(1, 100_000);
            samples.Add(new Sample(t, random.NextDouble() * 1000));
        }
        var decoded = GorillaCodec.Decode(GorillaCodec.Encode(samples), samples.Count);
        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void ConstantValuesRoundTrip()
    {
        var samples = Enumerable.Range(0, 200)
            .Select(i => new Sample(1_763_985_600_000L + i * 1000, 3.14))
            .ToArray();
        var encoded = GorillaCodec.Encode(samples);
        Assert.Equal(samples, GorillaCodec.Decode(encoded, samples.Length));
        Assert.True(encoded.Length < 100, $"Constant values should compress tiny, got {encoded.Length} bytes.");
    }

    [Fact]
    public void ExtremeValuesRoundTrip()
    {
        var samples = new[]
        {
            new Sample(1000, -1.7976931348623157E308),
            new Sample(2000, 1.7976931348623157E308),
            new Sample(3000, 0.0),
            new Sample(4000, -0.0),
            new Sample(5000, double.Epsilon),
            new Sample(6000, -123456.789),
        };
        Assert.Equal(samples, GorillaCodec.Decode(GorillaCodec.Encode(samples), samples.Length));
    }

    [Fact]
    public void RandomValuesRoundTrip()
    {
        var random = new Random(42);
        var samples = Enumerable.Range(0, 500)
            .Select(i => new Sample(1_763_985_600_000L + i * 1000, random.NextDouble() * 2 - 1))
            .ToArray();
        Assert.Equal(samples, GorillaCodec.Decode(GorillaCodec.Encode(samples), samples.Length));
    }
}

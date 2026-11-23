using System.Numerics;

namespace ChronoDB.Encoding;

/// <summary>
/// Gorilla compression (Facebook, 2015) for time-series chunks.
///
/// Timestamps use delta-of-delta encoding with variable-length control codes;
/// values use XOR with the previous value, storing only the meaningful bits.
/// On regular 1-second data this typically compresses to ~1.5 bytes/sample.
/// The codec is a pure function of the sample list: encoding then decoding
/// reproduces the exact same samples, bit for bit.
/// </summary>
public static class GorillaCodec
{
    public static byte[] Encode(IReadOnlyList<Sample> samples)
    {
        var writer = new BitWriter();
        if (samples.Count == 0)
            return Array.Empty<byte>();

        writer.WriteBits((ulong)samples[0].TimestampUnixMs, 64);
        writer.WriteBits((ulong)BitConverter.DoubleToInt64Bits(samples[0].Value), 64);

        var prevTimestamp = samples[0].TimestampUnixMs;
        var prevDelta = 0L;
        var prevValueBits = BitConverter.DoubleToInt64Bits(samples[0].Value);
        var prevLeading = 0;
        var prevTrailing = 0;
        var haveWindow = false;

        for (var i = 1; i < samples.Count; i++)
        {
            var sample = samples[i];

            // --- timestamp: delta-of-delta ---
            var delta = sample.TimestampUnixMs - prevTimestamp;
            var dod = delta - prevDelta;
            if (dod == 0)
            {
                writer.WriteBit(false);
            }
            else if (dod >= -63 && dod <= 64)
            {
                writer.WriteBits(0b10, 2);
                writer.WriteBits((ulong)(dod + 63), 7);
            }
            else if (dod >= -255 && dod <= 256)
            {
                writer.WriteBits(0b110, 3);
                writer.WriteBits((ulong)(dod + 255), 9);
            }
            else if (dod >= -2047 && dod <= 2048)
            {
                writer.WriteBits(0b1110, 4);
                writer.WriteBits((ulong)(dod + 2047), 12);
            }
            else
            {
                writer.WriteBits(0b1111, 4);
                writer.WriteBits((ulong)(uint)dod, 32);
            }
            prevDelta = delta;
            prevTimestamp = sample.TimestampUnixMs;

            // --- value: XOR with previous ---
            var bits = BitConverter.DoubleToInt64Bits(sample.Value);
            var xor = bits ^ prevValueBits;
            if (xor == 0)
            {
                writer.WriteBit(false);
            }
            else
            {
                var leading = BitOperations.LeadingZeroCount((ulong)xor);
                var trailing = BitOperations.TrailingZeroCount((ulong)xor);
                if (haveWindow && leading >= prevLeading && trailing >= prevTrailing)
                {
                    // Reuse the previous window: only the meaningful bits.
                    writer.WriteBits(0b10, 2);
                    writer.WriteBits((ulong)xor >> prevTrailing, 64 - prevLeading - prevTrailing);
                }
                else
                {
                    writer.WriteBits(0b11, 2);
                    writer.WriteBits((ulong)leading, 5);
                    var meaningful = 64 - leading - trailing;
                    writer.WriteBits((ulong)meaningful, 6);
                    writer.WriteBits((ulong)xor >> trailing, meaningful);
                    prevLeading = leading;
                    prevTrailing = trailing;
                    haveWindow = true;
                }
            }
            prevValueBits = bits;
        }

        return writer.ToArray();
    }

    public static Sample[] Decode(byte[] data, int count)
    {
        if (count == 0)
            return Array.Empty<Sample>();

        var reader = new BitReader(data);
        var samples = new Sample[count];

        var firstTimestamp = (long)reader.ReadBits(64);
        var firstValueBits = (long)reader.ReadBits(64);
        samples[0] = new Sample(firstTimestamp, BitConverter.Int64BitsToDouble(firstValueBits));

        var prevTimestamp = firstTimestamp;
        var prevDelta = 0L;
        var prevValueBits = firstValueBits;
        var prevLeading = 0;
        var prevTrailing = 0;

        for (var i = 1; i < count; i++)
        {
            // --- timestamp ---
            long dod;
            if (!reader.ReadBit())
                dod = 0;
            else if (!reader.ReadBit())
                dod = (long)reader.ReadBits(7) - 63;
            else if (!reader.ReadBit())
                dod = (long)reader.ReadBits(9) - 255;
            else if (!reader.ReadBit())
                dod = (long)reader.ReadBits(12) - 2047;
            else
                dod = (int)reader.ReadBits(32);

            var delta = prevDelta + dod;
            var timestamp = prevTimestamp + delta;
            prevDelta = delta;
            prevTimestamp = timestamp;

            // --- value ---
            double value;
            if (!reader.ReadBit())
            {
                value = BitConverter.Int64BitsToDouble(prevValueBits);
            }
            else
            {
                long xor;
                if (!reader.ReadBit())
                {
                    xor = (long)(reader.ReadBits(64 - prevLeading - prevTrailing) << prevTrailing);
                }
                else
                {
                    var leading = (int)reader.ReadBits(5);
                    var meaningful = (int)reader.ReadBits(6);
                    var trailing = 64 - leading - meaningful;
                    xor = (long)(reader.ReadBits(meaningful) << trailing);
                    prevLeading = leading;
                    prevTrailing = trailing;
                }
                value = BitConverter.Int64BitsToDouble(prevValueBits ^ xor);
            }
            prevValueBits = BitConverter.DoubleToInt64Bits(value);
            samples[i] = new Sample(timestamp, value);
        }

        return samples;
    }
}

using System.Text;
using ChronoDB.Encoding;

namespace ChronoDB.Storage;

/// <summary>
/// Immutable, Gorilla-compressed segment file holding one flush worth of series.
///
/// Layout: magic "CHDBSEG1", Int32 series count, then per series:
/// [Int32 keyLength][key UTF-8][Int32 sampleCount][Int32 payloadBytes][payload].
/// Segments are write-once: a flush writes a fresh segment containing the whole
/// in-memory state and deletes the previous ones, which also compacts away
/// any samples pruned by retention.
/// </summary>
internal static class Segment
{
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("CHDBSEG1");

    public static void Write(string path, IReadOnlyDictionary<SeriesKey, IReadOnlyList<Sample>> series)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(series.Count);
        foreach (var (key, samples) in series)
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(key.ToString());
            writer.Write(keyBytes.Length);
            writer.Write(keyBytes);
            writer.Write(samples.Count);
            var payload = GorillaCodec.Encode(samples);
            writer.Write(payload.Length);
            writer.Write(payload);
        }
        writer.Flush();
    }

    public static IEnumerable<(SeriesKey Key, Sample[] Samples)> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException($"Not a ChronoDB segment file: {path}");
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var keyLength = reader.ReadInt32();
            var key = SeriesKey.Parse(System.Text.Encoding.UTF8.GetString(reader.ReadBytes(keyLength)));
            var sampleCount = reader.ReadInt32();
            var payloadLength = reader.ReadInt32();
            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length < payloadLength)
                throw new InvalidDataException($"Truncated segment file: {path}");
            yield return (key, GorillaCodec.Decode(payload, sampleCount));
        }
    }
}

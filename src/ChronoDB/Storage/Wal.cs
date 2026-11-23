using System.Text;

namespace ChronoDB.Storage;

/// <summary>
/// Append-only write-ahead log. Every sample is durable before it is visible
/// in memory, so a crash loses nothing that was acknowledged.
///
/// Record layout: [Int32 keyLength][key UTF-8][Int64 timestamp][Double value].
/// A torn record at the tail (crash mid-write) is detected on replay and
/// ignored — replay stops there, since nothing after a torn record can be valid.
/// </summary>
internal sealed class Wal : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    public Wal(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    public long Length => _stream.Length;

    public void Append(SeriesKey key, Sample sample)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key.ToString());
        _writer.Write(keyBytes.Length);
        _writer.Write(keyBytes);
        _writer.Write(sample.TimestampUnixMs);
        _writer.Write(sample.Value);
        _writer.Flush();
        _stream.Flush();
    }

    /// <summary>Replay all complete records; stops silently at a torn tail record.</summary>
    public static IEnumerable<(SeriesKey Key, Sample Sample)> Replay(string path)
    {
        if (!File.Exists(path))
            yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        while (stream.Position < stream.Length)
        {
            int keyLength;
            byte[] keyBytes;
            long timestamp;
            double value;
            try
            {
                keyLength = reader.ReadInt32();
                if (keyLength <= 0 || keyLength > 1_000_000)
                    yield break;
                keyBytes = reader.ReadBytes(keyLength);
                if (keyBytes.Length < keyLength)
                    yield break;
                timestamp = reader.ReadInt64();
                value = reader.ReadDouble();
            }
            catch (EndOfStreamException)
            {
                yield break;
            }

            SeriesKey key;
            try
            {
                key = SeriesKey.Parse(System.Text.Encoding.UTF8.GetString(keyBytes));
            }
            catch (FormatException)
            {
                continue;
            }
            yield return (key, new Sample(timestamp, value));
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}

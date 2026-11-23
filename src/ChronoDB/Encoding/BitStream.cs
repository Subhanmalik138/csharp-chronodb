namespace ChronoDB.Encoding;

/// <summary>Writes individual bits into a byte array, most-significant bit first.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _bytes = new();
    private int _bitsUsed; // 0..7 bits already set in the last byte

    public void WriteBit(bool bit)
    {
        if (_bitsUsed == 0)
            _bytes.Add(0);
        if (bit)
            _bytes[^1] |= (byte)(1 << (7 - _bitsUsed));
        _bitsUsed = (_bitsUsed + 1) % 8;
    }

    /// <summary>Write the low <paramref name="count"/> bits of <paramref name="value"/>, MSB first.</summary>
    public void WriteBits(ulong value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            WriteBit(((value >> i) & 1UL) == 1UL);
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Reads individual bits from a byte array, most-significant bit first.</summary>
public sealed class BitReader
{
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos; // 0..7 bits already consumed in the current byte

    public BitReader(byte[] data) => _data = data;

    public bool ReadBit()
    {
        if (_bytePos >= _data.Length)
            throw new InvalidDataException("Unexpected end of compressed data.");
        var bit = ((_data[_bytePos] >> (7 - _bitPos)) & 1) == 1;
        _bitPos++;
        if (_bitPos == 8) { _bitPos = 0; _bytePos++; }
        return bit;
    }

    public ulong ReadBits(int count)
    {
        ulong value = 0;
        for (var i = 0; i < count; i++)
            value = (value << 1) | (ReadBit() ? 1UL : 0UL);
        return value;
    }
}

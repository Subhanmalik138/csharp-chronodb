namespace ChronoDB;

/// <summary>
/// Shared time parsing for the CLI and the HTTP API.
/// Accepts Unix milliseconds, ISO 8601, "now", and relative offsets like "-30m".
/// Durations accept a plain millisecond number or a suffixed value: 500ms, 30s, 5m, 1h, 7d.
/// </summary>
public static class TimeFormats
{
    public static long ParseTime(string text, long nowMs)
    {
        text = text.Trim();
        if (string.Equals(text, "now", StringComparison.OrdinalIgnoreCase))
            return nowMs;
        if (text.StartsWith('-') && text.Length > 1)
            return nowMs - ParseDurationMs(text[1..]);
        if (long.TryParse(text, out var ms))
            return ms;
        if (DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToUnixTimeMilliseconds();
        throw new FormatException(
            $"Cannot parse time '{text}'. Use Unix milliseconds, ISO 8601, 'now', or a relative offset like '-1h'.");
    }

    public static long ParseDurationMs(string text)
    {
        text = text.Trim();
        if (long.TryParse(text, out var ms))
            return ms;
        var suffixes = new (string Suffix, long Multiplier)[]
        {
            ("ms", 1),
            ("s", 1_000),
            ("m", 60_000),
            ("h", 3_600_000),
            ("d", 86_400_000),
        };
        foreach (var (suffix, multiplier) in suffixes)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(text[..^suffix.Length], out var amount))
                return amount * multiplier;
        }
        throw new FormatException(
            $"Cannot parse duration '{text}'. Use a millisecond number or a value like '30s', '5m', '1h', '7d'.");
    }

    public static string FormatUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'");
}

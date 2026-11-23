using System.Text;

namespace ChronoDB;

/// <summary>
/// Identifies one time series: a metric name plus a set of labels, e.g.
/// <c>http_requests_total{method="GET", status="200"}</c>.
///
/// Labels are stored sorted by key, so the canonical string form is stable
/// and can be used as a dictionary key and as a file-safe identity.
/// </summary>
public sealed class SeriesKey : IEquatable<SeriesKey>
{
    public string Metric { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }

    public SeriesKey(string metric, IReadOnlyDictionary<string, string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(metric))
            throw new ArgumentException("Metric name must not be empty.", nameof(metric));
        if (metric.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or ':' or '.')))
            throw new ArgumentException(
                "Metric name may only contain letters, digits, '_', ':' and '.'.", nameof(metric));

        Metric = metric;
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (labels is not null)
            foreach (var (k, v) in labels) sorted[k] = v;
        Labels = sorted;
    }

    /// <summary>Canonical form: <c>metric{label="value", ...}</c> with labels sorted.</summary>
    public override string ToString()
    {
        if (Labels.Count == 0)
            return Metric;
        var sb = new StringBuilder(Metric);
        sb.Append('{');
        var first = true;
        foreach (var (k, v) in Labels)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(k).Append("=\"").Append(Escape(v)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Parse a canonical series key such as <c>cpu{host="web-1", core="0"}</c>.</summary>
    public static SeriesKey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Series key must not be empty.");

        text = text.Trim();
        var brace = text.IndexOf('{');
        if (brace < 0)
        {
            if (!text.EndsWith('}'))
                return new SeriesKey(text);
            throw new FormatException($"Malformed series key: '{text}'.");
        }

        if (!text.EndsWith('}'))
            throw new FormatException($"Malformed series key: '{text}' (missing closing brace).");

        var metric = text[..brace].Trim();
        var inner = text[(brace + 1)..^1];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(inner))
        {
            foreach (var part in SplitLabels(inner))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException($"Malformed label '{part}' in series key '{text}'.");
                var name = part[..eq].Trim();
                var raw = part[(eq + 1)..].Trim();
                if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
                    throw new FormatException($"Label value for '{name}' must be double-quoted.");
                labels[name] = Unescape(raw[1..^1]);
            }
        }
        return new SeriesKey(metric, labels);
    }

    /// <summary>Split a label list on commas that are not inside quotes.</summary>
    private static IEnumerable<string> SplitLabels(string inner)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var escaped = false;
        foreach (var c in inner)
        {
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inQuotes) { sb.Append(c); escaped = true; continue; }
            if (c == '"') inQuotes = !inQuotes;
            if (c == ',' && !inQuotes) { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    public bool Equals(SeriesKey? other) =>
        other is not null && string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SeriesKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());
}

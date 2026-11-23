using System.Text.RegularExpressions;

namespace ChronoDB.Query;

/// <summary>
/// A label matcher inside a selector: <c>label="value"</c>, <c>label!="value"</c>,
/// <c>label=~"regex"</c> or <c>label!~"regex"</c>.
/// </summary>
public sealed class LabelMatcher
{
    public string Name { get; }
    public string Operator { get; }
    public string Pattern { get; }
    private readonly Regex? _regex;

    public LabelMatcher(string name, string op, string pattern)
    {
        Name = name;
        Operator = op;
        Pattern = pattern;
        if (op is "=~" or "!~")
            _regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    public bool Matches(string? value)
    {
        return Operator switch
        {
            "=" => value == Pattern,
            "!=" => value != Pattern,
            "=~" => value is not null && _regex!.IsMatch(value),
            "!~" => value is null || !_regex!.IsMatch(value),
            _ => throw new InvalidOperationException($"Unknown matcher operator '{Operator}'."),
        };
    }

    public override string ToString() => $"{Name}{Operator}\"{Pattern}\"";
}

/// <summary>
/// A query selector: an optional metric name plus label matchers, e.g.
/// <c>http_requests_total{method="GET", status!~"5.."}</c>.
/// An empty metric matches any metric; a selector with no matchers matches
/// every series with that metric.
/// </summary>
public sealed class Selector
{
    public string? Metric { get; }
    public IReadOnlyList<LabelMatcher> Matchers { get; }

    public Selector(string? metric, IReadOnlyList<LabelMatcher>? matchers = null)
    {
        Metric = string.IsNullOrWhiteSpace(metric) ? null : metric;
        Matchers = matchers ?? Array.Empty<LabelMatcher>();
    }

    public bool Matches(SeriesKey key)
    {
        if (Metric is not null && !string.Equals(key.Metric, Metric, StringComparison.Ordinal))
            return false;
        foreach (var matcher in Matchers)
        {
            key.Labels.TryGetValue(matcher.Name, out var value);
            if (!matcher.Matches(value))
                return false;
        }
        return true;
    }

    public static Selector Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Selector must not be empty.");
        text = text.Trim();

        var brace = text.IndexOf('{');
        if (brace < 0)
            return new Selector(text);

        if (!text.EndsWith('}'))
            throw new FormatException($"Malformed selector: '{text}' (missing closing brace).");

        var metric = text[..brace].Trim();
        var inner = text[(brace + 1)..^1];
        var matchers = new List<LabelMatcher>();
        if (!string.IsNullOrWhiteSpace(inner))
        {
            foreach (var part in SplitMatchers(inner))
            {
                var op = part.Contains("!~") ? "!~"
                    : part.Contains("=~") ? "=~"
                    : part.Contains("!=") ? "!="
                    : part.Contains('=') ? "="
                    : null;
                if (op is null)
                    throw new FormatException($"Malformed matcher '{part}' in selector '{text}'.");
                var opIndex = part.IndexOf(op, StringComparison.Ordinal);
                var name = part[..opIndex].Trim();
                var raw = part[(opIndex + op.Length)..].Trim();
                if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
                    throw new FormatException($"Matcher value for '{name}' must be double-quoted.");
                matchers.Add(new LabelMatcher(name, op, raw[1..^1]));
            }
        }
        return new Selector(metric, matchers);
    }

    private static IEnumerable<string> SplitMatchers(string inner)
    {
        var sb = new System.Text.StringBuilder();
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

    public override string ToString()
    {
        var metric = Metric ?? "";
        return Matchers.Count == 0
            ? metric
            : metric + "{" + string.Join(", ", Matchers) + "}";
    }
}

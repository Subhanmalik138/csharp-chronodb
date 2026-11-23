using System.Text.Json;
using ChronoDB;
using ChronoDB.Query;
using ChronoDB.Server;
using ChronoDB.Storage;

namespace ChronoDbCli;

/// <summary>
/// chronodb — command-line client for ChronoDB.
///
///   chronodb serve   [--data DIR] [--port N] [--retention 7d]
///   chronodb ingest  --metric M [--label k=v ...] [--value V] [--time T] [--file samples.jsonl]
///   chronodb query   --selector S [--from T] [--to T] [--step D] [--agg avg]
///   chronodb list    [--data DIR]
///   chronodb stats   [--data DIR]
///   chronodb flush   [--data DIR]
///
/// Times accept Unix milliseconds, ISO 8601, "now", or relative offsets ("-1h").
/// Durations accept milliseconds or a suffixed value ("30s", "5m", "1h", "7d").
/// </summary>
internal static class Program
{
    private const string DefaultDataDir = "./chronodb-data";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "serve" => Serve(args[1..]),
                "ingest" => Ingest(args[1..]),
                "query" => Query(args[1..]),
                "list" => List(args[1..]),
                "stats" => Stats(args[1..]),
                "flush" => Flush(args[1..]),
                "--help" or "-h" or "help" => Usage(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Serve(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        var port = int.Parse(Get(opts, "port", "8428"));
        var retention = GetOrNull(opts, "retention") is string retentionText
            ? TimeSpan.FromMilliseconds(TimeFormats.ParseDurationMs(retentionText))
            : (TimeSpan?)null;

        using var db = Database.Open(dataDir, new DatabaseOptions { Retention = retention });
        using var server = new ChronoServer(db, port);
        Console.WriteLine($"ChronoDB listening on http://localhost:{port}/ (data: {Path.GetFullPath(dataDir)})");
        Console.WriteLine("Press Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nStopping...");
            cts.Cancel();
            server.Stop();
        };
        server.Run();
        return 0;
    }

    private static int Ingest(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        var metric = GetOrNull(opts, "metric");
        var labels = ParseLabels(opts);

        using var db = Database.Open(dataDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (GetOrNull(opts, "file") is string file)
        {
            var count = 0;
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<JsonElement>(line);
                var entryMetric = entry.TryGetProperty("metric", out var m) ? m.GetString() : metric;
                if (string.IsNullOrWhiteSpace(entryMetric))
                    throw new FormatException("Each JSONL line needs a 'metric' (or pass --metric).");
                var entryLabels = new Dictionary<string, string>(labels);
                if (entry.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object)
                    foreach (var prop in l.EnumerateObject())
                        entryLabels[prop.Name] = prop.Value.GetString() ?? "";
                var key = new SeriesKey(entryMetric, entryLabels);
                db.Append(key, new Sample(
                    entry.GetProperty("t").GetInt64(),
                    entry.GetProperty("v").GetDouble()));
                count++;
            }
            Console.WriteLine($"Ingested {count} sample(s) from {file} into {dataDir}.");
            return 0;
        }

        if (metric is null)
            throw new FormatException("Ingest needs --metric (or --file with per-line metrics).");
        if (GetOrNull(opts, "value") is not string valueText || !double.TryParse(valueText, out var value))
            throw new FormatException("Ingest needs --value <number>.");
        var time = GetOrNull(opts, "time") is string timeText
            ? TimeFormats.ParseTime(timeText, now)
            : now;

        var singleKey = new SeriesKey(metric, labels);
        db.Append(singleKey, new Sample(time, value));
        Console.WriteLine($"Ingested 1 sample into {singleKey} at {TimeFormats.FormatUnixMs(time)}.");
        return 0;
    }

    private static int Query(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        var selectorText = GetOrNull(opts, "selector")
            ?? throw new FormatException("Query needs --selector, e.g. --selector 'cpu{host=\"web-1\"}'.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var to = GetOrNull(opts, "to") is string toText ? TimeFormats.ParseTime(toText, now) : now;
        var from = GetOrNull(opts, "from") is string fromText ? TimeFormats.ParseTime(fromText, now) : to - 3_600_000;
        var step = TimeFormats.ParseDurationMs(Get(opts, "step", "1m"));
        var agg = QueryEngine.ParseAggregation(Get(opts, "agg", "avg"));

        using var db = Database.Open(dataDir);
        var engine = new QueryEngine(db);
        var results = engine.RangeQuery(Selector.Parse(selectorText), from, to, step, agg);
        if (results.Count == 0)
        {
            Console.WriteLine("No matching series.");
            return 0;
        }
        foreach (var result in results)
        {
            Console.WriteLine(result.Key);
            if (result.Points.Count == 0)
            {
                Console.WriteLine("  (no samples in range)");
                continue;
            }
            foreach (var point in result.Points)
                Console.WriteLine($"  {TimeFormats.FormatUnixMs(point.TimestampUnixMs)}  {point.Value:G}");
        }
        return 0;
    }

    private static int List(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        using var db = Database.Open(dataDir);
        var keys = db.SeriesKeys;
        if (keys.Count == 0)
        {
            Console.WriteLine("No series yet.");
            return 0;
        }
        foreach (var key in keys)
        {
            var series = db.GetSeries(key)!;
            var latest = series.Latest;
            var earliest = series.Earliest;
            Console.WriteLine($"{key}  samples={series.Count}" +
                (latest is not null
                    ? $"  latest={latest.Value.Value:G} @ {TimeFormats.FormatUnixMs(latest.Value.TimestampUnixMs)}"
                    : "") +
                (earliest is not null
                    ? $"  first={TimeFormats.FormatUnixMs(earliest.Value.TimestampUnixMs)}"
                    : ""));
        }
        return 0;
    }

    private static int Stats(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        using var db = Database.Open(dataDir);
        var stats = db.Stats;
        Console.WriteLine($"Data directory : {Path.GetFullPath(dataDir)}");
        Console.WriteLine($"Series         : {stats.SeriesCount}");
        Console.WriteLine($"Total samples  : {stats.TotalSamples}");
        Console.WriteLine($"WAL size       : {stats.WalBytes} bytes");
        Console.WriteLine($"Segment size   : {stats.SegmentBytes} bytes ({stats.SegmentCount} segment(s))");
        return 0;
    }

    private static int Flush(string[] args)
    {
        var opts = ParseOptions(args);
        var dataDir = Get(opts, "data", DefaultDataDir);
        using var db = Database.Open(dataDir);
        db.Flush();
        var stats = db.Stats;
        Console.WriteLine($"Flushed. {stats.SegmentCount} segment(s), {stats.SegmentBytes} bytes, WAL truncated.");
        return 0;
    }

    private static int Usage()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            chronodb — ChronoDB time-series database CLI

            Usage:
              chronodb serve   [--data DIR] [--port N] [--retention 7d]
              chronodb ingest  --metric M [--label k=v ...] [--value V] [--time T] [--file samples.jsonl]
              chronodb query   --selector S [--from T] [--to T] [--step D] [--agg avg]
              chronodb list    [--data DIR]
              chronodb stats   [--data DIR]
              chronodb flush   [--data DIR]

            Times: Unix ms, ISO 8601, "now", or relative ("-1h"). Durations: "30s", "5m", "1h", "7d".
            Default data directory is ./chronodb-data.
            """);
    }

    /// <summary>Parse "--key value" and "--key=value" options; "--label" may repeat.</summary>
    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new FormatException($"Unexpected argument '{arg}'. Options use --key value.");
            var eq = arg.IndexOf('=');
            string key, value;
            if (eq >= 0)
            {
                key = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                key = arg[2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new FormatException($"Option '--{key}' needs a value.");
                value = args[++i];
            }
            if (!options.TryGetValue(key, out var list))
                options[key] = list = new List<string>();
            list.Add(value);
        }
        return options;
    }

    private static string Get(Dictionary<string, List<string>> opts, string key, string fallback) =>
        opts.TryGetValue(key, out var values) ? values[^1] : fallback;

    private static string? GetOrNull(Dictionary<string, List<string>> opts, string key) =>
        opts.TryGetValue(key, out var values) ? values[^1] : null;

    private static Dictionary<string, string> ParseLabels(Dictionary<string, List<string>> opts)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!opts.TryGetValue("label", out var rawLabels))
            return labels;
        foreach (var raw in rawLabels)
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0)
                throw new FormatException($"Label '{raw}' must be in k=v form.");
            labels[raw[..eq]] = raw[(eq + 1)..];
        }
        return labels;
    }
}

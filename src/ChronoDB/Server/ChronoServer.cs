using System.Net;
using System.Text;
using System.Text.Json;
using ChronoDB.Query;
using ChronoDB.Storage;

namespace ChronoDB.Server;

/// <summary>
/// Minimal HTTP API over <see cref="Database"/>, built on <see cref="HttpListener"/>
/// with zero external dependencies.
///
/// Endpoints:
///   GET  /health                  {"status":"ok"}
///   GET  /api/v1/series           list of series keys
///   GET  /api/v1/stats            storage statistics
///   GET  /api/v1/query/instant   ?selector=... [&amp;at=...]
///   GET  /api/v1/query            ?selector=...&amp;from=...&amp;to=...&amp;step=...&amp;agg=...
///   POST /api/v1/ingest           {"metric","labels":{},"samples":[{"t","v"}]} or a single {"t","v"}
/// </summary>
public sealed class ChronoServer : IDisposable
{
    private readonly Database _database;
    private readonly QueryEngine _engine;
    private readonly HttpListener _listener;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private bool _disposed;

    public int Port { get; }

    public ChronoServer(Database database, int port = 8428)
    {
        _database = database;
        _engine = new QueryEngine(database);
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>Serve requests on the calling thread until <see cref="Stop"/> is called.</summary>
    public void Run()
    {
        _listener.Start();
        try
        {
            while (!_disposed)
            {
                var context = _listener.GetContext(); // blocks; Stop() interrupts it
                _ = Task.Run(() => Handle(context));
            }
        }
        catch (HttpListenerException)
        {
            // Expected when Stop() closes the listener.
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Stop()
    {
        _disposed = true;
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose() => Stop();

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = request.HttpMethod;

            switch ((method, path))
            {
                case ("GET", "/health"):
                    WriteJson(response, 200, new { status = "ok" });
                    break;
                case ("GET", "/api/v1/series"):
                    WriteJson(response, 200, new
                    {
                        series = _database.SeriesKeys.Select(k => k.ToString()).ToArray(),
                    });
                    break;
                case ("GET", "/api/v1/stats"):
                    var stats = _database.Stats;
                    WriteJson(response, 200, new
                    {
                        seriesCount = stats.SeriesCount,
                        totalSamples = stats.TotalSamples,
                        walBytes = stats.WalBytes,
                        segmentBytes = stats.SegmentBytes,
                        segmentCount = stats.SegmentCount,
                    });
                    break;
                case ("GET", "/api/v1/query/instant"):
                    HandleInstantQuery(request, response);
                    break;
                case ("GET", "/api/v1/query"):
                    HandleRangeQuery(request, response);
                    break;
                case ("POST", "/api/v1/ingest"):
                    HandleIngest(request, response);
                    break;
                default:
                    WriteJson(response, 404, new { error = $"No route for {method} {path}." });
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException)
        {
            WriteJson(context.Response, 400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, 500, new { error = ex.Message });
        }
        finally
        {
            try { context.Response.Close(); } catch { /* already closed */ }
        }
    }

    private void HandleInstantQuery(HttpListenerRequest request, HttpListenerResponse response)
    {
        var selector = RequireSelector(request);
        var at = GetQuery(request, "at") is string atText
            ? TimeFormats.ParseTime(atText, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : (long?)null;
        var results = _engine.InstantQuery(selector, at)
            .Select(r => new
            {
                series = r.Key.ToString(),
                t = r.Sample.TimestampUnixMs,
                v = r.Sample.Value,
            }).ToArray();
        WriteJson(response, 200, new { results });
    }

    private void HandleRangeQuery(HttpListenerRequest request, HttpListenerResponse response)
    {
        var selector = RequireSelector(request);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var to = GetQuery(request, "to") is string toText ? TimeFormats.ParseTime(toText, now) : now;
        var from = GetQuery(request, "from") is string fromText
            ? TimeFormats.ParseTime(fromText, now)
            : to - 3_600_000; // default: last hour
        var step = GetQuery(request, "step") is string stepText ? TimeFormats.ParseDurationMs(stepText) : 60_000;
        var agg = GetQuery(request, "agg") is string aggText ? QueryEngine.ParseAggregation(aggText) : Aggregation.Avg;

        var results = _engine.RangeQuery(selector, from, to, step, agg)
            .Select(r => new
            {
                series = r.Key.ToString(),
                points = r.Points.Select(p => new { t = p.TimestampUnixMs, v = p.Value }).ToArray(),
            }).ToArray();
        WriteJson(response, 200, new { results });
    }

    private void HandleIngest(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!request.HasEntityBody)
            throw new FormatException("Ingest requires a JSON body.");
        var body = JsonSerializer.Deserialize<IngestRequest>(
            request.InputStream, _json)
            ?? throw new FormatException("Empty or invalid JSON body.");

        var labels = body.Labels ?? new Dictionary<string, string>();
        var key = new SeriesKey(body.Metric, labels);

        var samples = body.Samples is { Count: > 0 }
            ? body.Samples.Select(s => new Sample(s.T, s.V)).ToList()
            : body.T is not null && body.V is not null
                ? new List<Sample> { new(body.T.Value, body.V.Value) }
                : throw new FormatException("Ingest needs either 'samples' or a single 't' and 'v'.");

        _database.AppendRange(key, samples);
        WriteJson(response, 202, new { ingested = samples.Count, series = key.ToString() });
    }

    private static Selector RequireSelector(HttpListenerRequest request)
    {
        var text = GetQuery(request, "selector")
            ?? throw new FormatException("Missing required query parameter 'selector'.");
        return Selector.Parse(text);
    }

    private static string? GetQuery(HttpListenerRequest request, string name) =>
        request.QueryString[name];

    private void WriteJson(HttpListenerResponse response, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private sealed record IngestSample(long T, double V);

    private sealed record IngestRequest(
        string Metric,
        Dictionary<string, string>? Labels,
        List<IngestSample>? Samples,
        long? T,
        double? V);
}

using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChronoDB.Server;
using ChronoDB.Storage;

namespace ChronoDB.Tests;

public sealed class ServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronodb-stests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Database _db;
    private readonly ChronoServer _server;
    private readonly HttpClient _client;

    public ServerTests()
    {
        _db = Database.Open(_dir);
        var port = FreePort();
        _server = new ChronoServer(_db, port);
        _ = Task.Run(() => _server.Run());
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
        // Wait briefly for the listener to come up.
        for (var i = 0; i < 50 && !HealthOk(); i++)
            Thread.Sleep(100);
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private bool HealthOk()
    {
        try
        {
            return _client.GetAsync("health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task HealthReturnsOk()
    {
        var response = await _client.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task IngestThenQueryRoundTrip()
    {
        var ingest = new StringContent(
            """{"metric":"req_total","labels":{"method":"GET"},"samples":[{"t":1000,"v":10},{"t":2000,"v":20}]}""",
            System.Text.Encoding.UTF8, "application/json");
        var post = await _client.PostAsync("api/v1/ingest", ingest);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var query = await _client.GetAsync(
            "api/v1/query?selector=" + Uri.EscapeDataString("req_total{method=\"GET\"}") +
            "&from=0&to=3000&step=60s&agg=sum");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var body = await query.Content.ReadAsStringAsync();
        Assert.Contains("req_total", body);
        Assert.Contains("\"v\":30", body);
    }

    [Fact]
    public async Task SingleSampleIngest()
    {
        var ingest = new StringContent(
            """{"metric":"gauge","t":5000,"v":3.5}""",
            System.Text.Encoding.UTF8, "application/json");
        var post = await _client.PostAsync("api/v1/ingest", ingest);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Contains("\"ingested\":1", await post.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SeriesAndStatsEndpoints()
    {
        var ingest = new StringContent(
            """{"metric":"mem","t":1000,"v":1}""",
            System.Text.Encoding.UTF8, "application/json");
        await _client.PostAsync("api/v1/ingest", ingest);

        var series = await _client.GetAsync("api/v1/series");
        Assert.Contains("mem", await series.Content.ReadAsStringAsync());

        var stats = await _client.GetAsync("api/v1/stats");
        var body = await stats.Content.ReadAsStringAsync();
        Assert.Contains("\"seriesCount\":1", body);
        Assert.Contains("\"totalSamples\":1", body);
    }

    [Fact]
    public async Task BadSelectorReturns400()
    {
        var response = await _client.GetAsync("api/v1/query?selector=" + Uri.EscapeDataString("cpu{host=a}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRouteReturns404()
    {
        var response = await _client.GetAsync("api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IngestWithoutBodyReturns400()
    {
        var response = await _client.PostAsync("api/v1/ingest", new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

# ChronoDB

An embedded time-series database in C# (.NET 8, zero runtime dependencies).

ChronoDB stores metric samples — a value at a point in time, tagged with
labels — durably on disk, and answers instant and range queries with
downsampling. Think of it as a miniature Prometheus storage engine you can
embed in a .NET process or run as a small HTTP service: ingest samples,
ask for `cpu_usage{host="web-1"}` over the last hour averaged per minute,
and get points back.

## Features

- **Durable writes** — every sample is appended to a write-ahead log (and
  flushed) before it becomes visible, so acknowledged writes survive a crash.
- **Gorilla compression** — samples are stored in immutable segments using
  delta-of-delta timestamps and XOR value encoding (Facebook's Gorilla
  algorithm). Regular 1-second data compresses to well under half the raw
  16 bytes/sample.
- **Query language** — selectors like `http_requests_total{method="GET",
  status!~"5.."}` with `=`, `!=`, `=~`, `!~` label matchers; range queries
  with `avg`, `min`, `max`, `sum`, `count`, and counter `rate` aggregation
  over configurable downsampling steps.
- **HTTP API + CLI** — ingest and query over HTTP, or drive the database
  from the `chronodb` command line (`serve`, `ingest`, `query`, `list`,
  `stats`, `flush`).
- **Retention** — samples older than a configured age are pruned on open
  and compacted away on the next flush.
- **Safe with late data** — out-of-order samples are inserted in timestamp
  order, and a sample with an existing timestamp overwrites it
  (last-write-wins), which also makes WAL replay idempotent.

## Quick start

```bash
dotnet build ChronoDB.sln

# Start the server (data in ./chronodb-data, HTTP on :8428)
dotnet run --project src/chronodb -- serve

# Ingest a sample
dotnet run --project src/chronodb -- ingest --metric cpu_usage \
  --label host=web-1 --value 0.42

# Query the last hour, averaged per minute
dotnet run --project src/chronodb -- query \
  --selector 'cpu_usage{host="web-1"}' --from -1h --to now --step 1m --agg avg
```

Or talk to the server directly:

```bash
curl -X POST localhost:8428/api/v1/ingest -H 'Content-Type: application/json' \
  -d '{"metric":"http_requests_total","labels":{"method":"GET","status":"200"},
       "samples":[{"t":1763985600000,"v":120},{"t":1763985660000,"v":150}]}'

curl 'localhost:8428/api/v1/query?selector=http_requests_total&from=-1h&to=now&step=5m&agg=sum'
curl 'localhost:8428/api/v1/query/instant?selector=cpu_usage'
curl localhost:8428/api/v1/series
curl localhost:8428/api/v1/stats
```

Times accept Unix milliseconds, ISO 8601, `now`, or relative offsets
(`-30m`, `-1h`). Durations accept milliseconds or a suffixed value
(`500ms`, `30s`, `5m`, `1h`, `7d`).

## Design decisions

1. **WAL + immutable segments, not a single log.** The WAL is the
   durability story; segments are the compression story. A flush snapshots
   the whole in-memory state into one new Gorilla-compressed segment and
   truncates the WAL, which doubles as compaction — pruned samples never
   make it into the new segment, and a torn WAL tail is ignored on replay.
2. **Gorilla encoding, hand-rolled.** Timestamps compress with
   delta-of-delta; values with XOR against the previous value. Two edge
   cases the naive implementation gets wrong: the meaningful-bit length
   field is 6 bits, so a 64-bit value is stored as length − 1, and the
   leading-zero count is stored in 5 bits, so counts above 31 are clamped
   and the extra zeros ride along as meaningful bits. Both keep the codec
   bit-exact (see `GorillaCodecTests`).
3. **Buckets align to `from`, and empty buckets stay empty.** A range
   query splits `[from, to]` into step-sized buckets and aggregates each
   one; a bucket with no samples produces no point instead of an invented
   zero, so sparse series stay sparse. `rate` is the per-second increase
   of a counter, with counter resets handled (a decrease counts from zero).
4. **Zero dependencies, stdlib only.** Storage, compression, the HTTP
   server (`HttpListener`), and the CLI are all hand-rolled on the .NET
   base library. Nothing to restore beyond the test SDK, nothing to audit.

## HTTP API

| Method | Path | Description |
| ------ | ---- | ----------- |
| GET | `/health` | `{"status":"ok"}` |
| GET | `/api/v1/series` | All series keys |
| GET | `/api/v1/stats` | Series/sample counts, WAL and segment sizes |
| GET | `/api/v1/query/instant?selector=…[&at=…]` | Latest value per matching series |
| GET | `/api/v1/query?selector=…&from=…&to=…&step=…&agg=…` | Downsampled range query |
| POST | `/api/v1/ingest` | `{"metric","labels":{},"samples":[{"t","v"}]}` or a single `{"t","v"}` |

Errors are JSON `{"error": "…"}` with HTTP 400 for bad input and 404 for
unknown routes.

## CLI

```
chronodb serve   [--data DIR] [--port N] [--retention 7d]
chronodb ingest  --metric M [--label k=v ...] [--value V] [--time T] [--file samples.jsonl]
chronodb query   --selector S [--from T] [--to T] [--step D] [--agg avg]
chronodb list    [--data DIR]
chronodb stats   [--data DIR]
chronodb flush   [--data DIR]
```

`ingest --file` reads JSONL, one `{"metric","labels","t","v"}` object per
line. The default data directory is `./chronodb-data`.

## Storage layout

```
<data dir>/
  wal.log                 append-only log of every sample since the last flush
  segments/
    seg-<timestamp>-<id>.seg   one Gorilla-compressed snapshot per flush
```

A segment starts with the magic `CHDBSEG1`, then per series: the series
key, the sample count, and the Gorilla bitstream. Segments are write-once;
old ones are deleted once a newer flush has snapshotted the same state.

## Running the tests

```bash
dotnet test
```

67 xUnit tests cover the Gorilla codec (including bit-exact round-trips
for extreme doubles), WAL replay and torn-tail recovery, segment
flush/load, retention, selector matching, every aggregation, and the HTTP
API end to end.

## Project structure

```
ChronoDB.sln
src/
  ChronoDB/                  library (net8.0)
    Sample.cs                a (timestamp, value) observation
    SeriesKey.cs             metric + labels, canonical form and parsing
    TimeSeries.cs            in-memory sorted, thread-safe sample list
    TimeFormats.cs           shared time/duration parsing
    Encoding/
      BitStream.cs           bit-level reader/writer
      GorillaCodec.cs        Gorilla timestamp/value compression
    Storage/
      Wal.cs                 append-only write-ahead log
      Segment.cs             immutable compressed segment files
      Database.cs            open/replay/flush/retention orchestration
    Query/
      Selector.cs            metric + label matchers (=, !=, =~, !~)
      QueryEngine.cs         instant + range queries, aggregations
    Server/
      ChronoServer.cs        HttpListener JSON API
  chronodb/                  CLI (serve, ingest, query, list, stats, flush)
tests/
  ChronoDB.Tests/            xUnit test suite
```

## Tech stack

- C# 12 / .NET 8, no runtime dependencies
- `System.Text.Json` for the HTTP API, `HttpListener` for the server
- xUnit + Microsoft.NET.Test.Sdk for tests

## License

MIT — see [LICENSE](LICENSE).

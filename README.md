## ChronoDB

An embedded time-series database in C# (.NET 8, zero runtime dependencies).

Work in progress — see the commit history for the build stages:

1. Core domain model (`Sample`, `SeriesKey`, `TimeSeries`)
2. WAL + Gorilla-compressed segment storage
3. Query engine (selectors, aggregations, downsampling)
4. HTTP API server + CLI
5. xUnit test coverage
6. Documentation

---
summary: 'Bowire ships a built-in micro-benchmarker for any unary method.'
---

# Performance Graphs

Bowire ships a built-in micro-benchmarker for any unary method. Configure the request body in the **Body** tab as usual, then switch to the **Performance** tab in the response pane to repeat the call N times and visualize the latency distribution.

## When you can use it

The Performance tab is **only visible for unary methods** -- it appears as a third tab in the response pane next to **Response** and **Response Metadata**. Streaming, client-streaming, and duplex channels don't show it because they don't have a single-call latency to measure.

## Running a benchmark

1. Pick a unary method
2. Fill in the request body in the **Body** tab and any metadata in the **Metadata** tab
3. Switch to the **Performance** tab
4. Set **Calls** (1 -- 10000) and **Concurrency** (1 -- 20)
5. Click **Run benchmark**

While the run is in progress:

- The progress bar shows `completed / total calls` and a percentage
- The progress bar shimmers to indicate active work
- A **Stop** button appears -- click it to cancel cleanly. Stats compute from whatever calls already completed.
- Stats and charts update live every ~2 % of progress (capped to keep the UI responsive for large N)

## What gets measured

Each iteration runs through the **full Bowire pipeline**:

- `${var}` substitution against the active environment
- `${now}`, `${uuid}`, and other system variables -- regenerated **per call**, so timestamps and IDs differ across iterations
- Auth helper from the active environment -- including JWT signing and OAuth token caching
- Whatever metadata you set in the Metadata tab

Latency is measured as the wall-clock time from the moment the `/api/invoke` request leaves the browser to the moment the JSON response comes back. This includes Bowire's server-side dispatch overhead, but in practice that's small relative to gRPC round-trip time.

Failed calls (network errors, exceptions, error responses) **do not count** toward the latency stats -- they're tracked separately in the success / failed counters and the status distribution.

## Stats

After at least one successful call, the stats grid shows:

| Stat | Meaning |
|------|---------|
| **min** | Fastest call |
| **avg** | Arithmetic mean |
| **p50** | Median (50th percentile) |
| **p90** | 90th percentile |
| **p95** | 95th percentile |
| **p99** | 99th percentile |
| **max** | Slowest call |
| **throughput** | `success_count / total_seconds` (req/s) |
| **success** | Number of OK responses |
| **failed** | Network errors + non-success responses |
| **total** | Wall-clock duration of the whole run |
| **count** | Number of successful calls included in the percentile math |

Throughput is computed against the **wall-clock duration of the whole run**, so it reflects effective concurrency. With concurrency = 1 it's roughly `1000 / avg`. With concurrency = 10 it can be much higher if the server can handle parallel requests.

## Status distribution

Below the stats grid, every distinct status name (`OK`, `NotFound`, `Unavailable`, `Error`, `NetworkError`, ...) is listed with its occurrence count. Color-coded the same way as the action bar status indicators:

- **green** -- OK / success states
- **yellow** -- recoverable / client errors (NotFound, InvalidArgument, ...)
- **red** -- server errors and network failures

This is handy for catching intermittent failures: a 1000-call run that's "mostly fine" might still show 17 × `Unavailable` here.

## Latency histogram

The histogram bins all successful call latencies into 24 equal-width buckets between min and max. Each bar shows how many calls fell into that bucket. Hover any bar (the SVG `<title>` tooltip) for the exact range and count.

Two dashed vertical lines mark the **p50** (green) and **p95** (yellow) boundaries so you can read the shape of the distribution at a glance:

- **Tight cluster around p50, short tail** -- well-behaved service
- **Long right tail past p95** -- some calls are way slower than typical (GC pause? cold cache?)
- **Bimodal (two peaks)** -- two distinct paths through the code, e.g. cache hit vs miss

## Latency over time

The second chart plots latency against call index as a polyline. Same p50 / p95 markers, but as horizontal lines this time, so you can see how individual calls compare to the percentile boundaries.

Patterns to look for:

- **Flat line near p50** -- consistent performance
- **First few calls slow, then drops** -- warm-up effect (JIT, connection pool, cache priming)
- **Periodic spikes** -- background work, GC, or downstream throttling
- **Increasing trend** -- memory leak, connection exhaustion, or backpressure building up

For sequential runs (concurrency = 1) the X axis is also a time axis. With concurrency > 1 the indices reflect completion order, not start order, so the picture is fuzzier.

## Memory

Bowire keeps **only the per-call latency numbers**, never the response bodies, so memory stays bounded even for very large N. A 10 000-call run uses roughly 80 KB of latency data plus the SVG render.

The benchmark state is **in memory only** and is reset whenever you start a new run. There's no export today -- copy the stats values manually if you need to log them somewhere.

## Tips

- **Use small N first** -- start with 100 calls to spot configuration mistakes before running 5000.
- **Watch the Console** -- the [Console / Log View](console.md) gets a single REQ entry at the start of the benchmark and a RES entry at the end with the totals. Individual calls are not logged (they would flood the buffer).
- **Bump concurrency carefully** -- the browser fetches go out simultaneously up to the concurrency limit. With high concurrency you're benchmarking your network stack and the server's ability to handle parallel requests, not single-call latency.
- **Combine with [Environments](environments.md)** -- benchmark the same call against Dev, then Staging, then Prod by switching environments. The results are not retained across switches, so screenshot or copy the stats first.
- **Compare percentiles, not averages** -- the average can hide a long tail. p99 is the number that matters for user-facing latency.

---
title: Performance
summary: 'Bowire ships a built-in micro-benchmarker for any unary method. v2.1 ships the rail as `Kuestenlogik.Bowire.Benchmarking` (renamed from `Rail.Benchmarks`).'
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

The benchmark state is **in memory only** and is reset whenever you start a new run. Export a finished run as CSV, k6-summary JSON or OTLP metrics JSON from the run's menu if you need to keep it.

## From the CLI -- gating a pipeline on latency

The rail measures; `bowire bench run` measures **and gates**. Same method call, same percentile arithmetic (nearest-rank, so the number you read in the rail is the number CI compares against):

```bash
bowire bench run Weather/getCurrent -url rest@http://localhost:6000 \
  -n 500 -c 8 --warmup 20 \
  --threshold "p95 < 200" \
  --threshold "error-rate < 0.01" \
  --fail-on-threshold
```

```
  20 ok · 0 failed · 59.2 req/s · 337 ms total
  min 0.25ms   p50 0.5ms   p90 0.87ms   p95 3.96ms   p99 322ms   max 322ms   avg 16.74ms

  Thresholds
    FAIL  p95<0.001   (actual 3.96ms)
```

| Flag | Meaning |
|------|---------|
| `-n`, `--iterations` | How many calls to make (default 50). |
| `-c`, `--concurrency` | Calls in flight at once (default 1). |
| `--warmup` | Calls made and discarded first, so JIT and connection setup stay out of the percentiles. |
| `--threshold` | A budget: `metric operator value`. Repeatable. |
| `--fail-on-threshold` | Exit non-zero when any budget is breached -- the CI gate. |
| `--k6-summary <file>` | Write the run as k6-summary JSON, thresholds included. |

Metrics you can set a budget on: `p50`, `p90`, `p95`, `p99`, `avg`, `min`, `max`, `error-rate` (0..1) and `throughput` (calls/second). k6's own `p(95)` spelling parses too, so a budget can be copied straight out of a k6 script.

The `--k6-summary` export carries each budget the way k6 reports its own -- keyed by its source text inside the metric it constrains:

```json
"http_req_duration": {
  "values": { "p(95)": 3.9571, "…": 0 },
  "thresholds": { "p95<200": { "ok": true } }
}
```

so a dashboard that already ingests k6 summaries finds Bowire's budgets where it looks for k6's.

### Per-request budget or aggregate budget?

Both exist, and they answer different questions:

- A **flow expectation** (`kind: latency`) is a **per-request** budget: *this specific call must answer within N ms*, checked on every execution of that step in `bowire test`. Use it when one slow call is a functional failure.
- A **benchmark threshold** is an **aggregate** budget over many calls: *the p95 across 500 calls must stay under N ms*. Use it when the distribution is what matters -- a single slow call is noise, a shifted p95 is a regression.

Reach for the flow expectation inside a functional suite, and for the benchmark threshold in a performance job.

## Tips

- **Use small N first** -- start with 100 calls to spot configuration mistakes before running 5000.
- **Watch the Console** -- the [Console / Log View](console.md) gets a single REQ entry at the start of the benchmark and a RES entry at the end with the totals. Individual calls are not logged (they would flood the buffer).
- **Bump concurrency carefully** -- the browser fetches go out simultaneously up to the concurrency limit. With high concurrency you're benchmarking your network stack and the server's ability to handle parallel requests, not single-call latency.
- **Combine with [Workspaces](workspaces.md)** -- benchmark the same call against Dev, then Staging, then Prod by switching workspaces. The results are not retained across switches, so screenshot or copy the stats first.
- **Compare percentiles, not averages** -- the average can hide a long tail. p99 is the number that matters for user-facing latency.

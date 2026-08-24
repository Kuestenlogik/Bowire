---
title: <fill in before the tag>
version: 2.5.0
---

<One-sentence frame for what 2.5 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern:
### <headline> (#issue)
<2-4 sentences>
-->

### The contract matrix — consumer × provider at a glance (#364)

`bowire contract verify` told you about one contract at a time. With a
handful of consumers against a handful of providers, "is anything broken
right now?" meant reading a pile of CI logs. Every verify run now stores
its verdict under `.bowire/contract-results/`, and that store backs a
matrix: rows are consumers, columns providers, each cell carries
pass/fail, how many interactions held, and when it last ran. Pairs nobody
verified show as blanks rather than silently missing.

The rollup is available from all four surfaces off one shared engine: the
**Contracts** rail in the workbench (grid plus drill-in to a failing
interaction's shape diff), `bowire contract matrix` (text grid, `--json`,
and `--fail-on-failures` as a CI gate), `GET /api/contracts/matrix`, and
the `bowire.contract.matrix` MCP tool, which hands an agent only what
broke per failing cell. Reading the matrix never contacts a provider — it
reports what verify already stored, so opening the rail cannot trigger an
outbound call.

Getting there meant lifting the verification engine out of the CLI
assembly, where it was `internal` and unreachable for the endpoint and
MCP, into a new **`Kuestenlogik.Bowire.Contracts`** package. It sits above
`Kuestenlogik.Bowire.Flows` (verification reuses the structural snapshot
comparer), ships the rail descriptor and its JS fragment, and is bundled
into the standalone tool — so `bowire contract` is unchanged and the rail
is simply there. Embedded hosts opt in by referencing the package, like
every other rail.

### Latency budgets that fail a pipeline (#360)

Benchmarks were informational: the workbench rail measured p50/p95/p99 and
drew the graphs, but the numbers lived in the browser, so no pipeline could
fail on a latency regression — the one thing k6 gets reached for. `bowire
bench run` puts the same measurement on the command line and adds budgets:

```bash
bowire bench run Weather/getCurrent -url rest@http://localhost:6000 \
  -n 500 -c 8 --warmup 20 \
  --threshold "p95 < 200" --threshold "error-rate < 0.01" \
  --fail-on-threshold
```

A breached budget is marked in the summary with what it actually measured,
and `--fail-on-threshold` turns that into a non-zero exit. Budgets read
`metric operator value` over p50/p90/p95/p99/avg/min/max/error-rate/
throughput; k6's own `p(95)` spelling parses too, so a threshold can be
copied straight out of a k6 script.

`--k6-summary` writes the run in the shape the rail already exports (#234),
with each budget attached the way k6 reports its own — keyed by source text
inside the metric it constrains, with an `ok` flag — so a dashboard that
ingests k6 summaries finds Bowire's budgets where it looks for k6's.

The measurement itself is deliberately not a second implementation: the
runner drives `IBowireProtocol.InvokeAsync`, the same path `bowire call` and
`bowire test` take, percentiles use the rail's nearest-rank method, and
success/failure follows the same rule the workbench history uses. A p95 read
in the rail is the p95 CI compares against.

### One view over a portfolio of services (#587)

Per-service findings answer "is this service healthy?" — the rollup answers
it for a whole portfolio, and it needs nothing new to be produced. Every
signal it shows is an artefact some Bowire command already writes: lint
findings, contract-verification results, benchmark runs and k6 summaries,
scan SARIF, test JUnit.

```bash
bowire report rollup --from reports/ --fail-on high
```

```
  SERVICE      WORST    LINT (H/M/L)   CONTRACTS   TESTS       P95       LAST
  billing-api  HIGH     —              0/1         —           312ms     2026-08-20
  gateway      HIGH     —              —           —           —         —
  orders-api   MEDIUM   0/1/1          1/1         42/42       —         2026-08-19
```

An em dash means **there is no such report**, never zero: a service nobody
has linted must not read as a clean bill of health. In JSON the same
distinction is `null` versus a number.

Reports are attributed to services without configuration — a contract files
under its **provider** (the service under test, not the consumer), and
otherwise the first path segment that isn't storage layout decides, so a CI
job that collects reports into `reports/<service>/` just works.

The same rollup is in the workbench as the **Rollup** rail (click a row to
see which files fed it) and as the `bowire.report.rollup` MCP tool, all three
emitting the same JSON. Reading it never contacts a service; it reports what
is already on disk.

This is the read-and-aggregate half of the org dashboard. The hosted platform
— upload endpoint, retained history, org login, admin actions — remains #188.

### A long subscription no longer buries the message you are reading

Watching a busy stream — a gRPC server-stream, an SSE ticker, an MQTT topic
— used to get worse the longer you watched. Past a couple of dozen frames
the message list stopped scrolling and simply grew, pushing the detail pane
off the bottom of the window. There was no scrollbar to get back to it, so
the one thing the list exists for — seeing what a message actually contains
— became unreachable exactly when the stream was busiest.

The list scrolls inside its own pane again and the detail pane stays put.
The cause was a wrapper element in the default (non-split) streaming path
that carried no sizing rules at all, which voided the height constraint on
the stream output nested inside it; the previous fix for this symptom was
applied one level too low to take effect there. A browser-level regression
test now holds the geometry — output fits its pane, list scrolls, detail
body stays on screen — and it is verified to fail when the rule is removed.

### The response toolbar trades labels for icons

**Copy**, **Download**, **Expand all** and **Collapse all** are now icon
buttons. The response action row is the densest in the workbench, and those
four labels restated glyphs that carry the meaning on their own; dropping
the text gives the row roughly 200px back. The wording is not gone — it is
the hover tooltip and the accessible name of each button, and Download in
fact gained a tooltip it never had. **Use this...** keeps its label,
because the cluster has exactly one primary action and it should still read
as one.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

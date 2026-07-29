---
title: Correlated timeline
summary: 'Read a multi-protocol recording as one transaction: a lane per protocol on a shared time axis, each step verdicted against a correlation key Bowire resolves from headers or payloads.'
---

# Correlated Timeline

A recording that walks one business transaction across gRPC, OData,
REST, GraphQL, WebSocket, SignalR, SSE and MQTT looks, in the step list,
like eight unrelated rows. The **Correlated timeline** tab in the
Recordings detail pane reads the same recording as one transaction:
one lane per protocol, one bar per step, per-frame ticks for streaming
steps, all on a shared time axis — and each step marked as belonging to
the transaction or not.

Open it from **Recordings → pick a recording → Correlated timeline**, or
from the recording's right-click menu in the sidebar. The same analysis
is available in the terminal as
[`bowire recording correlate`](#cli).

## Where the signal comes from

Bowire recordings carry no trace id. Nothing in the
[`.bwr` format](../recordings/bwr-format.md) has ever had one, and no
capture path writes one. So the timeline resolves a signal in three
tiers, and says which one it used:

```mermaid
flowchart TD
    A[Recording] --> B{Correlation header<br/>on step.metadata?}
    B -->|yes| C["key = header value<br/>source: header"]
    B -->|no| D{"Same id-shaped JSON leaf<br/>on 2+ steps?"}
    D -->|yes| E["key = best-scoring leaf<br/>source: field"]
    D -->|no| F["no key<br/>source: none —<br/>lanes only, no verdicts"]
```

1. **A correlation header.** `traceparent`, `x-correlation-id`,
   `correlation-id`, `x-request-id`, `request-id`, `x-trace-id`,
   `trace-id` — matched case- and separator-insensitively on
   `step.metadata`. For `traceparent` the **trace-id** (field 2 of
   `00-<32 hex>-<16 hex>-<flags>`) is used, not the whole header: the
   span-id changes per hop and would never correlate. A header always
   outranks an inferred field, because it is an explicit statement by
   the producer.
2. **A shared id-shaped payload field.** Every scalar JSON leaf whose
   name ends in `id` is grouped by (name, value). Groups spanning fewer
   than two steps are dropped, and the bare name `id` is never
   *suggested* (it collides across every entity in a multi-service
   capture) — though you can still pick it by hand. Candidates are
   scored `protocols × 1000 + steps`, so a value that spans five
   services beats one repeated five times inside a single response.
3. **Nothing.** The whole recording is treated as one transaction. You
   still get the lanes, the axis and the frame ticks — you just get no
   per-step verdict, and the view says so instead of pretending.

The chosen key is shown as a chip at the top of the tab. Click it to
pick a different one from the ranked candidate list, or to go back to
**Auto — best guess**.

## Strong, weak, and unmatched

| Tier | Rendered as | Rule |
|---|---|---|
| **strong** | Solid accent bar | Some leaf's normalised name *ends with* the key's normalised name **and** carries the key's value. `shipId` therefore matches `onShipId` and `OccupiedByShipId`. |
| **weak** | Dashed outline | The value turned up on some *other* id-shaped leaf. |
| **unmatched** | Faded bar | The key does not appear in this step at all. |

The weak tier exists because low-cardinality ids collide. In the harbor
sample, `portCallId = 1`, `craneId = 1` and dock number `1` are three
different things wearing the same number — a value-only match would fuse
them. Requiring the *name* to match for the strong tier keeps that
honest, and the weak tier keeps a genuine hit (the gRPC step's bare
`"id": 101`) visible without claiming more than it should.

A header key has no weak tier: a header either carries the id or it does
not.

Streaming steps also get one tick per received frame, verdicted the same
way and placed at *step offset + frame timestamp*. Click any bar or tick
to pin a detail strip with the step's offset, duration, status and
match; click it again to unpin.

## Timebase

Recordings come in two flavours and the view will not mix them up:

- **absolute** — `capturedAt` is wall-clock epoch milliseconds, as a
  live workbench capture writes it. The stats line shows the origin as a
  clock time.
- **relative** — `capturedAt` is an offset from an arbitrary zero, which
  is what authored sample recordings use (`0, 150, 300, …`). No
  wall-clock is printed, because there is none.

Either way, offsets are normalised to `capturedAt − min(capturedAt)`. A
recording where *every* step has `capturedAt = 0` falls back to
cumulative durations, so the lanes still read as a sequence, and the
view raises a note saying the axis shows elapsed work rather than real
spacing.

## What it looks like on the harbor sample

The flagship `port-call-1` recording resolves `shipId = 101` and reports
**6 of 8 steps across 6 protocols**:

| Protocol | Verdict | Why |
|---|---|---|
| `odata`, `rest`, `websocket`, `signalr`, `sse` | strong | Carry `OccupiedByShipId` / `onShipId` / `ShipId` / `shipId` = 101 |
| `grpc` | weak | Carries `"id": 101` — right value, generic name |
| `graphql` | unmatched | The id lives inside a query *string*, not as a JSON leaf |
| `mqtt` | unmatched | Crane telemetry only knows `craneId` |

That is the honest number, and it is what the tab shows. Correlating the
last two needs multi-key joins (`shipId → portCallId → craneId`), which
is deliberately out of scope for this stage — see
[Not yet](#not-yet).

## Persisting the key

Picking a key writes it onto the recording as an optional `correlation`
field:

```json
{
  "id": "rec_...",
  "name": "Port call 1",
  "correlation": { "name": "shipId", "value": "101" },
  "steps": [ ... ]
}
```

The field is additive and diagnostic-only — nothing in the replay, mock
or matcher path reads it, and writing it does **not** bump
`recordingFormatVersion`. Its only job is to make the workbench, the CLI
and a colleague who opens the file next week agree on the same key
without re-picking it. See
[`.bwr` format → Recording root](../recordings/bwr-format.md).

## CLI

```bash
bowire recording correlate ./port-call-1.bwr
bowire recording correlate ./port-call-1.bwr --key shipId=101
bowire recording correlate ./port-call-1.bwr --json | jq '.matchedStepCount'
```

| Flag | Meaning |
|---|---|
| `<path>` | The `.bwr` to read. Both envelope shapes are accepted. |
| `--name` | Disambiguate a store-wrapped file carrying several recordings. |
| `--key name=value` | Correlate on this key instead of the auto-detected one. Overrides any persisted `correlation` field. |
| `--json` | Emit the full model instead of the table — byte-comparable with what `POST /api/recordings/correlate` returns. |

Exit codes are the same sysexits set `bowire recording validate` uses:
`0` ok, `64` bad args, `65` malformed file, `66` file not found, `70`
I/O error.

Default output is a table — offset, protocol, service / method,
duration, status, match — followed by the runner-up candidate keys and
any notes. No ASCII bar art: a terminal table is the honest CLI form of
this view.

## Importing a recording

The Recordings rail could always import HAR but never its own format,
which meant a `.bwr` from a colleague, from `bowire har convert`, or
from this workbench's own **Export → JSON** was openable everywhere
except the workbench that writes it. **Import .bwr** now sits next to
**Import HAR** in the recording toolbar and accepts both envelope
shapes.

## How it is wired

The analysis lives once, in C#, as
`RecordingCorrelationAnalyzer` in the
`Kuestenlogik.Bowire.Recordings` package. It is pure and stateless: the
same inputs always produce the same model, which is what lets the
terminal and the browser make identical claims.

The workbench reaches it over a stateless
`POST {prefix}/api/recordings/correlate`, mounted through the
`IBowireEndpointContribution` seam — so it lands at
`/api/recordings/correlate` standalone and
`/bowire/api/recordings/correlate` under an embedded
`MapBowire("/bowire")`, and inherits the host's auth gate either way.
The request carries the recording document rather than an id, which also
covers an in-progress capture that has not been flushed to disk yet.

A host that does not reference the Recordings package has no Recordings
rail at all and never sees any of this; core carries only the optional
`correlation` field on the recording model.

## Not yet

Deliberately out of scope for this stage, so nobody has to re-litigate
it:

- **Multi-key joins.** `shipId 101 → portCallId 1 → craneId 1` is what
  would light up all eight harbor lanes. Stage 1 is one key with a
  strong/weak/none verdict per step.
- **GraphQL query parsing.** The id inside `{ portCall(id: 1) { … } }`
  is inside a string, not a JSON leaf. The response side still
  contributes.
- **Causality.** No parent/child span arrows — the format carries no
  parent link to draw them from.
- **A real `traceId` field in the format**, and OTLP trace ingestion.
  The `Monitoring.Otlp` and `Protocol.Otlp` packages exist, but neither
  carries trace data into recordings today.
- **Live correlation of the Console**, rather than a saved recording.
- Zoom / pan on the axis, cross-recording correlation, exporting the
  timeline as an image, and any Map-rail linkage.

## Implementation references

- `src/Kuestenlogik.Bowire.Recordings/Correlation/RecordingCorrelationAnalyzer.cs` — the analysis
- `src/Kuestenlogik.Bowire.Recordings/Correlation/RecordingCorrelationScanner.cs` — leaf walking + header lookup
- `src/Kuestenlogik.Bowire.Recordings/Correlation/RecordingCorrelationEndpoints.cs` — `POST /api/recordings/correlate`
- `src/Kuestenlogik.Bowire.Recordings/wwwroot/js/recording-correlation.js` — the tab
- `src/Kuestenlogik.Bowire.Tool/Cli/RecordingCommand.cs` — `bowire recording correlate`

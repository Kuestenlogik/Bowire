# Bowire v2.7 — acceptance record

Run on 2026-09-07 against the tool built from `6d10904e`, one sample at a
time, workbench on port 5179.

## What "pass" means here

`/api/invoke` answers HTTP 200 whenever the *plugin ran*; the protocol's own
verdict rides in the response's `status` field. The first pass of this run
counted HTTP 200 alone and reported 11/11 — read properly, that pass had gRPC
on `InvalidArgument`, SignalR on `Error`, JSON-RPC on `-32601`, and WebSocket
and SSE politely refusing the unary surface. Discovery and dispatch were
proven; the call was not.

Everything below is scored on `status`, with a payload the method's own
schema accepts.

## Phase C — protocols against their samples

| Protocol | Discovery | Call | Evidence |
|----------|-----------|------|----------|
| REST | 1 svc | ok | `GET /pets` → 3 pets; `POST /pets` → id 4 |
| gRPC | 1 svc / 5 methods | ok | `SayHello{"name":"world"}` → `Hello, world!` |
| GraphQL | 3 svc / 4 methods | ok | `books` query returned data |
| SignalR | 1 svc / 2 methods | ok | `invoke{"method":"Echo","args":["hello"]}` → `echo: hello` |
| SSE | 1 svc | stream 200 | streaming surface answered; no frame captured in a short read |
| WebSocket | 1 svc | not exercised | channel-only; the harness does not speak the channel |
| JSON-RPC | 1 svc / 3 methods | ok | `add{"a":2,"b":3}` → `5` |
| OData | 2 svc / 10 methods | ok | entity set returned |
| MCP | 3 svc / 5 methods | ok | `echo{"text":…}` → `echo: hello from acceptance` |
| SOAP | 1 svc / 4 methods | ok | `Add` → SOAP 1.1 envelope |
| MQTT | 1 svc / 2 methods | ok | published 35 B to `bowire/sample/sensor`, qos 1 |
| NATS | 1 svc | ok | published 35 B to `bowire.echo` |
| Pulsar | 1 svc | ok | produced to `persistent://public/default/bowire-sample`, message_id `9:53:…` |

**11 of 13 protocols confirmed with a real call.** WebSocket and SSE each
refused the unary surface *correctly*, naming the surface to use instead;
driving those two needs a channel client and a streaming reader, which this
harness does not have. That gap is in the harness, not in the plugins.

The plugin-hint marker (`__bowirePluginHint`) appears in **no** response from
any protocol — the metadata migration verified end to end, not only in unit
tests.

### Plugins that are not in the workbench bundle

`bowire plugin install --file <pkg>.nupkg --source https://api.nuget.org/v3/index.json`

- **NATS** (NATS.Net), **Pulsar** (DotPulsar, Google.Protobuf) — third-party
  dependencies, so the optional-package rule explains them.
- **SOAP** — no third-party dependency at all, and the only in-repo protocol
  outside the bundle without one. Its sample's header prints
  `bowire --url soap@…` with no install step and the docs list it under
  "First-party protocols" unqualified, so a reader has no way to know. Open
  question for the maintainer: bundle it, or say so in the sample and docs.

## Phase D — embedded variant

`MapBowire("/bowire")` in a host that serves its own API.

- `/bowire` and `/bowire/` → 200; the host's own `/api/users` → 200.
- **In-process discovery works**: the embedded workbench enumerated its own
  host's REST surface (`GET /api/users`, `POST /api/users`) without going
  over the wire — the "In-Prozess-Oberfläche, same contract surface, no
  transport" case.
- `/bowire/bowire.js` 404 — not a defect; the page references no external
  script, assets are inlined.
- `/bowire/mcp` 404 — not a defect; a deliberate `<link rel="mcp">` discovery
  hint, unconditional by design ("a 404 on /mcp is a cheaper signal than
  misleading absence").

## Phase E — CLI / HTTP parity on the pinned surfaces

The SSE and SignalR ad-hoc surfaces appear only when the caller named the
plugin, which makes them the sharp test of the hint reaching a plugin.

| Surface | Result |
|---------|--------|
| `GET /api/services?serverUrl=signalr@…` | `SignalR Hub`, 2 methods |
| `bowire discover --url signalr@…` | `SignalR Hub  (2 methods, via signalr)` |

Before `6d10904e` the CLI found nothing here: only `/api/services` merged the
hint into the discovery metadata, so `bowire discover`, `bowire invoke` and
both MCP discovery tools were denied the pinned-only surfaces the workbench
got. The merge now happens once, in `BowireDiscoveryProbe`.

## Observations worth a ticket (none release-blocking)

1. **Method-name form is not uniform across plugins.** Pulsar accepts only
   `fullName` (`pulsar/topic/<name>/produce`) and rejects `name`; MCP, gRPC
   and JSON-RPC accept only `name` and reject `fullName` (`Unknown tool:
   'Tools/echo'`, `-32601`, `502`). An API caller reading a discovery
   response the obvious way hits one or the other. The workbench never
   notices, because it has learned the right form per plugin.
2. **SignalR's declared input schema disagrees with the plugin.** Discovery
   declares `args:string`; the plugin requires a JSON array and says so
   clearly (`"args" must be an array…`). The error message is excellent; the
   schema is what misleads.
3. **`plugin install` suggests a remedy its own guard blocks.** A package
   whose runtime dependencies could not be resolved installs anyway, with a
   warning to "Re-run with --source". That re-run then hits "already
   installed" without mentioning the `uninstall` step in between.
4. **The REST sample's discovery includes Bowire's own endpoints.**
   `GET__bowire_api_users`, `…_me`, `…_migration` sit alongside the sample's
   `/pets`, because the sample hosts the workbench and its OpenAPI document
   covers both. Explainable, but noisy for a first-time reader.

## Test suites at the same commit

`Kuestenlogik.Bowire.Tests` 3284, `Protocol.SignalR.Tests` 71,
`Protocol.Sse.Tests` 49 — all green, three consecutive full runs after two
test-isolation defects were fixed (see `6d10904e`).

---
summary: 'Turn any Bowire recording into a standalone HTTP mock server that replays the captured responses — no live backend required.'
---

# Mock Server

Turn any Bowire recording into a standalone HTTP mock server that replays the captured responses — no live backend required. Useful for frontend development against a reproducible API, for CI test fixtures, and for demoing a service without standing up the real stack.

**Scope (current release)**: REST and gRPC unary, reactive replay. Path-template matching (`/users/{id}`) is live as of Phase 2a. Streaming playback, timed proactive emission, and dynamic response-body substitution are [on the roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#replay-mock-server--recordings-as-api-endpoints) as Phase 2 follow-ups.

## Two ways to run

### 1. Standalone, via the CLI

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire mock --recording scenario.bwr --port 7070
```

| Flag | Default | Notes |
|---|---|---|
| `--recording, -r <path>` | &mdash; | Required. Accepts both a single-recording file and the full `~/.bowire/recordings.json` store. |
| `--port, -p <port>` | `6000` | Listen port. |
| `--host <addr>` | `127.0.0.1` | Use `0.0.0.0` for LAN / sidecar-container setups. |
| `--select <name-or-id>` | &mdash; | Disambiguates a store file that contains multiple recordings. Matches on `name` or `id`. |
| `--no-watch` | off | Disable hot-reload on file changes. |

`Ctrl+C` shuts the server down cleanly.

### 2. Embedded, via ASP.NET middleware

Reference the `Kuestenlogik.Bowire.Mock` package and call `UseBowireMock(...)` on your pipeline. Typical test-fixture usage:

```csharp
using Kuestenlogik.Bowire.Mock;

var builder = WebApplication.CreateBuilder();
builder.Services.AddControllers();
var app = builder.Build();

app.UseBowireMock("tests/fixtures/payments-happy.bwr", opts =>
{
    opts.PassThroughOnMiss = true;  // unmatched requests fall through to the live pipeline
    opts.Watch = false;              // tests shouldn't race with file watches
});

app.MapControllers(); // live fallback when PassThroughOnMiss = true
```

With `PassThroughOnMiss = true` (the default), the mock serves whatever matches and lets everything else reach the rest of the pipeline. Set it to `false` to return a `404` for every unmatched request — good when the mock is meant to be the only responder.

The middleware must run **before** the real service mappings it shadows:

```csharp
app.UseBowireMock("fixtures/users.json", o => o.PassThroughOnMiss = true);  // ← first
app.MapGet("/users/{id}", (int id) => ...);                                   // ← live, only hit on miss
```

## How matching works (Phase 1b)

The default `ExactMatcher` handles two protocol families:

- **REST** — matches recorded steps by `(httpVerb, httpPath)`. The path is either a **literal** (`/weather` → string compare, case-sensitive per the HTTP spec) or an **OpenAPI-style template** (`/users/{id}` → each `{name}` binds to one path segment; `/files/{name}.txt` → `.` stays a literal dot). Templates are auto-detected when the recorded `httpPath` contains `{`.
- **gRPC** — matches requests whose `Content-Type` is `application/grpc*` by the `/{service}/{method}` URL path (where `service` is the fully-qualified protobuf service name, e.g. `/calc.v1.Calculator/Add`).

The first matching unary step wins. Non-unary steps are skipped by the matcher — they fall through to the pipeline or produce a 404. Phase 2 adds path-template matching (`/users/{id}`), topic matching (MQTT / Socket.IO wildcards), streaming replay, and proactive emission.

### Protocol selection on the standalone listener

The standalone `bowire mock` CLI can't serve both plaintext HTTP/1.1 and plaintext HTTP/2 on the same port (ALPN isn't available without TLS), so it picks one based on what the recording contains:

- **Recording has at least one gRPC step** &rarr; the listener speaks HTTP/2 (prior-knowledge). gRPC clients work out of the box. REST clients hitting the same port need HTTP/2 too — `curl --http2-prior-knowledge http://127.0.0.1:<port>/path`.
- **Recording is REST-only** &rarr; the listener speaks HTTP/1.1. Plain `curl` / `httpie` / browser fetch work.

The embedded middleware path (`UseBowireMock` on your own app) is unaffected — whatever protocols your host's Kestrel binding supports will work.

## Recording format

Recordings are JSON documents captured by the Bowire UI's recorder (see [Recorder](recording.md)). The mock accepts two shapes:

1. **Single-recording file** &mdash; one recording at the top level, typically checked into the repo next to the code it mocks.
2. **Recordings-store file** &mdash; the full `~/.bowire/recordings.json` envelope. Pass `--select <name>` if the store contains more than one recording.

Minimal sample with a single REST step:

```json
{
  "id": "rec_weather_happy",
  "name": "Weather service — happy path",
  "recordingFormatVersion": 1,
  "steps": [
    {
      "id": "step_current",
      "protocol": "rest",
      "service": "WeatherService",
      "method": "GetCurrentWeather",
      "methodType": "Unary",
      "httpVerb": "GET",
      "httpPath": "/api/weather/current",
      "status": "OK",
      "response": "{\"temp\":21.5,\"condition\":\"clear\",\"city\":\"Berlin\"}"
    }
  ]
}
```

Save the file, then:

```bash
bowire mock --recording weather.bwr --port 7070
```

And hit it:

```bash
curl -i http://127.0.0.1:7070/api/weather/current
# HTTP/1.1 200 OK
# Content-Type: application/json; charset=utf-8
# {"temp":21.5,"condition":"clear","city":"Berlin"}
```

## Dynamic values in response bodies

Recorded REST response bodies can carry placeholders that the mock substitutes per-request. Matches the Bowire UI's auth-helper variable syntax so the same `${...}` vocabulary works in both places:

| Token | Replaced with |
|---|---|
| `${uuid}` | Fresh UUID v4 per substitution |
| `${now}` | Current Unix timestamp in seconds |
| `${nowMs}` | Current Unix timestamp in milliseconds |
| `${now+N}` / `${now-N}` | `${now}` shifted by `N` seconds (e.g. `${now+3600}` for one hour ahead) |
| `${timestamp}` | Current UTC time as ISO 8601 with millisecond precision (`2026-04-22T14:35:12.478Z`) |
| `${random}` | Random `uint32`, rendered in decimal |

Each occurrence is resolved independently, so `{"a":"${uuid}","b":"${uuid}"}` yields two different UUIDs. Unknown tokens (e.g. `${foo}`) are left verbatim, so literal `${...}`-shaped content in a recorded body survives unchanged.

Example step:

```json
{
  "id": "step_create_user",
  "protocol": "rest",
  "methodType": "Unary",
  "httpVerb": "POST",
  "httpPath": "/users",
  "status": "Created",
  "response": "{\"id\":\"${uuid}\",\"createdAt\":${now},\"validUntil\":${now+86400}}"
}
```

gRPC responses are skipped — they're binary protobuf and a text substitution pass would break the wire format. If you need dynamic values in a gRPC mock, shape the originating recording so the captured protobuf already carries the intended values; future phases may add schema-aware gRPC substitution.

## SSE streaming replay

Recorded **REST server-streaming** sessions replay as `text/event-stream` responses. The mock writes each frame the recorder captured into the stream as a `data: <payload>\n\n` event, paced by the per-frame `timestampMs` that the recorder stored (Phase-1a groundwork).

A minimal streaming step:

```json
{
  "id": "step_events",
  "protocol": "rest",
  "methodType": "ServerStreaming",
  "httpVerb": "GET",
  "httpPath": "/events",
  "status": "OK",
  "receivedMessages": [
    { "index": 0, "timestampMs": 0,     "data": { "type": "start" } },
    { "index": 1, "timestampMs": 1200,  "data": { "type": "tick", "n": 1 } },
    { "index": 2, "timestampMs": 2400,  "data": { "type": "tick", "n": 2 } },
    { "index": 3, "timestampMs": 2450,  "data": { "type": "end" } }
  ]
}
```

Client hits `GET /events` → mock emits four SSE events over ~2.5 seconds in the recorded rhythm. Each event carries `id: <index>` alongside its `data:` line so browsers automatically track where they are. The outer envelope that Bowire's own recorder used (`{ index, data, timestampMs }`) is otherwise stripped from the wire output; what the client sees on the SSE stream is the inner `data` payload — the exact shape the original backend produced.

Dynamic-value substitution applies to streamed frames too, so every `${uuid}`, `${now}`, etc. in the frame's `data` is resolved fresh on every replay.

### Reconnect resume via `Last-Event-ID`

SSE clients send `Last-Event-ID: <n>` when they reconnect after a dropped connection — the last `id:` they received. The mock reads that header, skips every frame whose `Index` is `<= n`, and resumes from the next one. A client that disconnected after frame 2 and reconnects with `Last-Event-ID: 2` picks up at frame 3 as if the drop never happened.

Edge cases:

- **Header absent or empty** → replay from the first frame, as normal.
- **Non-numeric value** → ignored (our recorder only writes integers). Full replay from the start.
- **Value past the end** → empty stream (client thinks it's caught up; mock agrees).

No recorder-side changes needed; the `Index` field has been on every captured frame since Phase-1a.

Steps captured before Phase 2c don't carry the `receivedMessages` array; the mock returns a `501` with a clear "re-record against a current Bowire" message in that case rather than serving an empty stream silently.

### gRPC server-streaming replay

Recorded **gRPC server-streaming** (and duplex) sessions replay natively as real gRPC over HTTP/2. Each frame's captured wire bytes (`responseBinary` under `receivedMessages`) are emitted with the correct 5-byte length-prefix framing and paced by the per-frame `timestampMs`. The final `grpc-status` / `grpc-message` trailer is written after the last frame, mapped from the recorded status using the same table as unary gRPC.

Recording shape for a streaming step:

```json
{
  "id": "step_count",
  "protocol": "grpc",
  "service": "stream.Counter",
  "method": "Count",
  "methodType": "ServerStreaming",
  "status": "OK",
  "receivedMessages": [
    { "index": 0, "timestampMs": 0,     "data": "\"alpha\"", "responseBinary": "CgVhbHBoYQ==" },
    { "index": 1, "timestampMs": 400,   "data": "\"beta\"",  "responseBinary": "CgRiZXRh" },
    { "index": 2, "timestampMs": 800,   "data": "\"gamma\"", "responseBinary": "CgVnYW1tYQ==" }
  ]
}
```

The outer `response` field on the step is ignored for streaming replay; only `receivedMessages[].responseBinary` is sent on the wire. Like REST SSE replay, `MockOptions.ReplaySpeed` controls pacing.

gRPC clients built with `Grpc.Net.Client` consume the mocked stream exactly as they would a live backend: `AsyncServerStreamingCall` + `ResponseStream.MoveNext()` loop works unchanged.

### WebSocket duplex replay

Recorded **WebSocket** sessions replay as real WebSocket upgrades. When a client opens `ws://<mock>/<path>`, the mock accepts the handshake and pushes the captured server-to-client frames back in order, paced by each frame's `timestampMs`. Incoming client-to-server frames are accepted and discarded — Phase 2e covers the server-push direction; input-driven replay (gate on matching a `sentMessages` entry before emitting the next batch) is a later phase.

Frame format mirrors what `WebSocketBowireChannel` wraps incoming frames in:

```json
{
  "id": "step_chat",
  "protocol": "websocket",
  "service": "WebSocket",
  "method": "/ws/chat",
  "methodType": "Duplex",
  "httpPath": "/ws/chat",
  "httpVerb": "GET",
  "receivedMessages": [
    { "index": 0, "timestampMs": 0,    "data": { "type": "text",   "text": "hello" } },
    { "index": 1, "timestampMs": 400,  "data": { "type": "binary", "base64": "3q2+7w==" } },
    { "index": 2, "timestampMs": 800,  "data": { "type": "text",   "text": { "msg": "world" } } }
  ]
}
```

Text frames are emitted with `WebSocketMessageType.Text`; binary frames with `WebSocketMessageType.Binary`. A text envelope whose `text` field is itself an object (e.g. `{ "msg": "world" }`) is sent as its compact JSON string — the same shape the original backend produced before `WebSocketBowireChannel` parsed it. `ReplaySpeed` controls pacing identically to SSE / gRPC streaming. Substitution (`${uuid}`, `${now}`, …) applies to text frames; binary frames pass through verbatim.

Client example using `System.Net.WebSockets`:

```csharp
using var client = new ClientWebSocket();
await client.ConnectAsync(new Uri("ws://127.0.0.1:6000/ws/chat"), CancellationToken.None);

var buffer = new byte[4096];
while (client.State is WebSocketState.Open)
{
    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    if (result.MessageType == WebSocketMessageType.Close) break;
    Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, result.Count));
}
```

SignalR and Socket.IO are built on top of WebSocket but add their own wire-level protocols (JSON-RPC-ish framing for SignalR, custom ACK-tagged JSON for Socket.IO). Replaying those requires understanding those protocols — planned as separate slices in Phase 2.

## Proactive MQTT emission

Recordings that contain MQTT publish steps trigger a second listener alongside the HTTP server: an **embedded MQTTnet broker** that the mock publishes into on its own schedule. No HTTP trigger involved — subscribers connect once, the mock fires the recorded publishes paced by each step's `capturedAt` offset, subscribers receive them as if the original backend were still running.

A typical MQTT step looks like:

```json
{
  "id": "step_temperature",
  "capturedAt": 1724840102000,
  "protocol": "mqtt",
  "service": "sensors",
  "method": "sensors/temperature",
  "methodType": "Unary",
  "body": "21.5",
  "metadata": { "qos": "1", "retain": "false" },
  "status": "OK"
}
```

- `method` &rarr; the MQTT topic.
- `body` (or the first entry of `messages`) &rarr; the payload; dynamic-value substitution (`${uuid}`, `${now}`, …) applies.
- `metadata.qos` &rarr; QoS level as the name (`AtLeastOnce`) or the numeric code (`0`/`1`/`2`); defaults to `AtLeastOnce` when absent.
- `metadata.retain` &rarr; `"true"` to set the retain flag.

Ports:

| Option | Meaning |
|---|---|
| `MockServerOptions.MqttPort = 1883` | Listen on the MQTT well-known port (default). |
| `MockServerOptions.MqttPort = 0` | OS-assigned. Read the actual port back from `MockServer.MqttPort` after `StartAsync`. |
| `MockServerOptions.EnableMqtt = false` | Don't start the broker even if the recording has MQTT steps (e.g. when running alongside a production broker on the same machine). |

The emitter applies a short 250 ms grace period before the first publish so a subscriber that hasn't completed its `CONNECT` + `SUBSCRIBE` round-trip doesn't miss the opening burst on non-retained topics. The full replay uses `MockOptions.ReplaySpeed` for pacing — `0` fires everything after the grace period instantly, `1.0` respects the original wall-clock deltas between `capturedAt` timestamps.

Connect with any MQTT client:

```csharp
var factory = new MqttClientFactory();
using var client = factory.CreateMqttClient();

await client.ConnectAsync(factory.CreateClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", mockServer.MqttPort)
    .Build());

await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter("sensors/#")
    .Build());

client.ApplicationMessageReceivedAsync += args =>
{
    Console.WriteLine(args.ApplicationMessage.Topic);
    return Task.CompletedTask;
};
```

Phase 2f is one-shot — when the scheduler reaches the last publish, it stops. Looped replay (continuously replay the sequence while the mock runs) is a candidate follow-up.

## Other protocols that replay via existing paths

The mock dispatches replay strategies by wire-level characteristics, not by the protocol label on the step. Any recorded step that has an `httpPath` + `httpVerb` goes through the REST / SSE replayer regardless of how the originating plugin tagged it. That means these protocols replay correctly today without their own code path:

- **OData** &mdash; canonical REST with specific response shapes (`$metadata`, `@odata.context`, entity sets). Recordings of OData calls replay verbatim through the REST unary path.
- **MCP (Model Context Protocol)** &mdash; streamable-HTTP transport is a `POST /mcp` that returns either a single JSON response (replayed as REST unary) or a framed SSE stream for server-pushed notifications (replayed as SSE). Both directions work today.

- **GraphQL subscriptions (`graphql-transport-ws`)** &mdash; the mock speaks the handshake (`connection_init` &rarr; `connection_ack`), reads the client's `subscribe` frame to capture whatever id it assigns, then replays the recorded `next`/`complete`/`error` frames with that id rewritten on every outgoing frame. Works transparently for any `graphql-ws` / Apollo / urql client. If a recording stopped before a `complete` was captured, the replayer synthesises one so the subscription ends cleanly. See [GraphQL subscription replay](#graphql-subscription-replay) below.

What **doesn't** work without protocol-aware handling (tracked separately):

- **SignalR / Socket.IO** &mdash; each has its own framing and control protocol on top of WebSocket. Generic WS replay gets most of the way but misses negotiation semantics. Separate slices when demand appears.

### GraphQL subscription replay

A recorded GraphQL subscription step carries the `next` / `complete` / `error` frames the recorder's client received, each wrapped in the standard WebSocket-channel envelope:

```json
{
  "id": "step_book_sub",
  "protocol": "graphql",
  "service": "GraphQL",
  "method": "Subscription.bookAdded",
  "methodType": "ServerStreaming",
  "httpPath": "/graphql",
  "httpVerb": "GET",
  "receivedMessages": [
    { "index": 0, "timestampMs": 0,   "data": { "type": "text", "text": { "type": "next",     "id": "rec-7b3c", "payload": { "data": { "bookAdded": { "title": "Moby Dick" } } } } } },
    { "index": 1, "timestampMs": 500, "data": { "type": "text", "text": { "type": "next",     "id": "rec-7b3c", "payload": { "data": { "bookAdded": { "title": "Dune" } } } } } },
    { "index": 2, "timestampMs": 900, "data": { "type": "text", "text": { "type": "complete", "id": "rec-7b3c" } } }
  ]
}
```

The id baked into every recorded frame is whatever the *recorder's* client picked at capture time — opaque to the server. A fresh client will assign its own id during `subscribe`, and the replay rewrites the id on each outgoing frame to match so the client accepts the data. The replay sequence:

1. Client opens `ws://<mock>/graphql` with the `graphql-transport-ws` subprotocol.
2. Client sends `{"type":"connection_init"}`; mock replies `{"type":"connection_ack"}`.
3. Client sends `{"type":"subscribe","id":"<client-id>","payload":{...}}`; mock captures `<client-id>`.
4. Mock emits each recorded `next`/`error` frame with `id` rewritten to `<client-id>`, paced by `timestampMs`.
5. Mock emits `complete` with the same id (synthesised if the recording missed it).

Client example with [`graphql-ws`](https://github.com/enisdenjo/graphql-ws):

```js
import { createClient } from 'graphql-ws';

const client = createClient({ url: 'ws://127.0.0.1:6000/graphql' });

client.subscribe(
    { query: 'subscription { bookAdded { title } }' },
    {
        next: (data) => console.log(data),
        error: (err) => console.error(err),
        complete: () => console.log('done')
    }
);
```

### Playback speed

`MockOptions.ReplaySpeed` controls the frame pacing:

| Value | Behaviour |
|---|---|
| `1.0` (default) | Original cadence — frame deltas as captured |
| `2.0` | Twice as fast |
| `0.5` | Half speed |
| `0` | All frames emitted immediately, no delays |

For the CLI the speed is fixed to 1.0 today; a `--speed` flag is on the Phase-2 polish list.

## Chaos injection

`MockOptions.Chaos` (or `--chaos` on the CLI) adds latency jitter and a configurable failure rate to matched responses, so a mock can double as a local resilience-testing tool without standing up a full chaos-engineering stack.

| Key | Meaning |
|---|---|
| `latency:<ms>` | Fixed delay in milliseconds before every matched response. |
| `latency:<min>-<max>` | Random delay in the `[min, max]` range per request. |
| `fail-rate:<0..1>` | Probability of failing the request before it hits the replayer. `0.05` = 5%. |
| `fail-status:<code>` | Override the HTTP status for fail-rate hits. Default: `503`. |

Chaos only fires on a **matched** step — unmatched paths still see a clean miss (404 or pass-through), so a high `fail-rate` doesn't mask gaps in the recording coverage. Latency runs before the replayer starts, so for streaming responses it delays the first byte; the captured frame cadence (or `ReplaySpeed`) still controls the gaps between frames.

```bash
# Inject 100–500 ms of jitter and fail 5% of requests with a 503.
bowire mock --recording happy.json --chaos "latency:100-500,fail-rate:0.05"

# Fixed 250 ms delay, no failures.
bowire mock --recording happy.json --chaos "latency:250"

# 10% of requests become 500s.
bowire mock --recording happy.json --chaos "fail-rate:0.1,fail-status:500"
```

Programmatic use from an embedded host looks the same:

```csharp
app.UseBowireMock(recording, opts =>
{
    opts.Chaos = new Kuestenlogik.Bowire.Mock.Chaos.ChaosOptions
    {
        LatencyMinMs = 100,
        LatencyMaxMs = 500,
        FailRate = 0.05
    };
});
```

Chaos draws from `Random.Shared`, which means percentages are approximate over many requests — a 5% fail-rate is not "exactly 1 in 20", it's 5% per request independently. For deterministic tests, set `FailRate = 1.0` (always fail) or `FailRate = 0` (never fail) and use a fixed latency (`LatencyMinMs == LatencyMaxMs`).

## Stateful mode

In the default stateless mode, every request matches against the entire recording — if two steps both cover `GET /flow`, the first one always wins. `MockOptions.Stateful` (or `--stateful` / `--stateful-once` on the CLI) switches to **strict-order** mode: only the step at the current cursor can match, and every hit advances the cursor to the next step. That turns a recording into a scripted multi-step workflow.

| Flag | End-of-recording behaviour |
|---|---|
| `--stateful` | Loops back to step 0 after the last step. |
| `--stateful-once` | Returns a miss (404 or pass-through per `PassThroughOnMiss`) for every request after the last step. |

An out-of-order request in stateful mode always misses — the cursor does not scan ahead. That's the feature: if your test expects the client to call step 1 before step 2, an accidental jump to step 2 surfaces as a clean 404 instead of quietly matching the first step that happens to fit.

The cursor resets to 0 when the recording file is hot-reloaded (`RecordingWatcher` fires `ReplaceRecording`), since the new file defines a fresh step sequence.

```bash
# Walk through happy-path.json in order, looping after the last step.
bowire mock --recording happy-path.json --stateful

# Same, but stop responding after step N.
bowire mock --recording happy-path.json --stateful-once
```

Programmatic use:

```csharp
app.UseBowireMock(recording, opts =>
{
    opts.Stateful = true;
    opts.StatefulWrapAround = true; // default
});
```

Stateful mode composes with chaos injection: both knobs apply to whichever step the cursor selects, so you can latency-jitter a scripted flow or fail 5% of the strict-ordered requests without extra wiring.

## Capturing misses

`MockOptions.CaptureMissPath` (or `--capture-miss <path>` on the CLI) turns the mock into a spec-authoring tool. Every unmatched REST request is appended to the named file as a placeholder `BowireRecordingStep` — the verb, path, headers, and body are persisted; response and status are left blank for the user to fill in.

```bash
# Exercise the API, collect what wasn't covered, turn the misses into new steps.
bowire mock --recording happy.json --capture-miss misses.json
# ... run your tests against the mock ...
# Open misses.json, write the responses for each captured step, save.
# Merge misses.json into happy.json (or point the mock at it directly).
```

The capture file is a standard single-recording document, so it can be replayed by the mock as-is once responses are filled in:

```bash
bowire mock --recording misses.json --port 7070
```

Notes:

- **REST only.** gRPC misses are skipped — binary protobuf bodies can't be faithfully captured as text without a schema descriptor. Capture those via the Bowire UI's recorder instead.
- **Appends, never clobbers.** Subsequent misses add new steps; existing content is preserved.
- **Hop-by-hop headers are filtered** (`Connection`, `Transfer-Encoding`, pseudo-headers like `:authority`, …) — they reflect the current connection, not the request the user would want to replay.
- **Body cap: 1 MiB.** Larger bodies are truncated with a trailing marker so the capture file doesn't balloon on binary uploads.
- **Composes with pass-through.** When `PassThroughOnMiss = true` is also set, the miss is written *before* the request is forwarded, so the capture succeeds even if the downstream handler consumes the body stream.

## Schema-only mock

Not every API has a recording to replay — sometimes you just want a stub server from a schema. `MockServerOptions.SchemaPath` (or `--schema <path>` on the CLI) loads an OpenAPI 3 document (JSON or YAML) and synthesises a recording at startup: every operation becomes one step whose response body is generated from the declared response schema.

```bash
bowire mock --schema weather.openapi.yaml --port 7070
```

The generator is type-aware:

| Schema | Sample |
|---|---|
| `integer` | `1` (or `minimum` when set) |
| `number` | `1.5` |
| `string` | `"sample"`, or format-aware (`date-time` → `"2026-01-01T00:00:00Z"`, `uuid` → zeroed GUID, `email` → `sample@example.com`, `uri` → `https://example.com`, …) |
| `boolean` | `true` |
| `array` | 3 items of the inner schema |
| `object` | recursive — every defined property emitted |
| `enum` | first value |

`example` and `default` on the schema always win over the generated guess, so authors can pin specific sample values in the spec itself. Recursion depth is capped at 8 so cyclic `$ref` trees stop cleanly instead of looping.

All the other mock knobs (chaos, stateful mode, miss-capture) apply unchanged — as far as the middleware is concerned, the synthesised recording is just a recording:

```bash
bowire mock --schema api.yaml --chaos "latency:50-200,fail-rate:0.05"
bowire mock --schema api.yaml --stateful
```

Scope:

- **OpenAPI only.** gRPC/proto and GraphQL schema-only modes are candidates for future slices — their response encoding (binary protobuf, typed GraphQL trees) needs a larger generator.
- **First success response wins.** When an operation declares several 2xx responses, the mock picks the first JSON-typed one. Branching by `Accept` header is a later enhancement.
- **Mutually exclusive with `--recording`.** Pick one. To mix real and synthesised responses, capture the missing ones via `--capture-miss` and merge into your recording.

## Hot-reload

By default the mock watches the recording file. Save a change in your editor and the in-memory routes rebuild without a restart; parse failures are logged and the previously loaded recording keeps serving. Disable with `--no-watch` (CLI) or `opts.Watch = false` (embedded).

## Logging

Every incoming request produces one structured log entry:

- `match(step=<id>, protocol=<p>, service=<s>, method=<m>) → <status>` &mdash; info-level, on a successful match.
- `no-match(path=<p>, method=<v>) → pass-through` &mdash; debug-level, when passthrough served the request.
- `no-match(path=<p>, method=<v>) → 404` &mdash; warning-level, when passthrough is off.
- `unsupported(step=<id>, methodType=<t>) → 501 — streaming replay is Phase 2` &mdash; warning-level, for the defensive fallthrough inside the replayer.
- `reloaded(path=<p>, steps=<n>, version=<v>)` &mdash; info-level, after a successful hot-reload.

The standalone host prints to the console; embedded use goes through whatever `ILoggerFactory` the host has registered.

## Common workflows

### Offline development

Point your frontend at the mock server while the real backend is unavailable:

```bash
# Record a session against the real server
bowire --url https://api.example.com
# (use the UI recorder, export as JSON)

# Later, replay offline
bowire mock --recording session.json --port 6000
```

### CI fixtures

Check recording files into the repo and spin up a mock in CI for deterministic integration tests:

```yaml
- name: Start mock server
  run: bowire mock --recording fixtures/api-smoke.json --port 6000 &

- name: Run integration tests
  run: dotnet test --filter Category=Integration
  env:
    API_URL: http://localhost:6000
```

### Demos and presentations

Recorded responses are stable and repeatable. Point your demo client at the mock and you get the same result every run, regardless of what the live backend is doing.

### Environment switching

Combine with [Environments](environments.md): set `${baseUrl}` to `http://localhost:6000` in a "Mock" environment and flip between mock and real backend from the Bowire UI without touching the client code.

## gRPC replay

When the Bowire recorder captures a gRPC call, it stores both the JSON rendering of the response (for UI display) and the raw protobuf wire bytes (base64-encoded in the step's `responseBinary` field). On replay the mock writes the wire bytes verbatim with the correct gRPC framing — no runtime protobuf re-encoding, and no need for the user to ship `.proto` files alongside the recording. A minimal gRPC step looks like this:

```json
{
  "id": "step_add",
  "protocol": "grpc",
  "service": "calc.Calculator",
  "method": "Add",
  "methodType": "Unary",
  "status": "OK",
  "response": "{\"sum\":42}",
  "responseBinary": "CCo="
}
```

The `service` and `method` fields map directly to the gRPC URL path `POST /calc.Calculator/Add`. The `responseBinary` carries the serialised output message. The `response` JSON is kept for diffing and inspection; the mock doesn't read it at replay time.

gRPC recordings use `recordingFormatVersion: 2`. Older (`v1`) recordings don't carry the binary payload; the mock returns `501` with a clear re-record-required message if it's asked to replay a gRPC step without one.

### gRPC Server Reflection

When the Bowire recorder captures a gRPC service, it also stashes the proto `FileDescriptorSet` for the service and its transitive dependencies alongside the call. Both fields travel into the recording step:

```json
{
  "id": "step_add",
  "protocol": "grpc",
  "service": "calc.Calculator",
  "method": "Add",
  "methodType": "Unary",
  "status": "OK",
  "responseBinary": "CCo=",
  "schemaDescriptor": "<base64 FileDescriptorSet>"
}
```

On startup the mock collects every unique `schemaDescriptor` in the loaded recording, builds a live descriptor pool, and exposes **gRPC Server Reflection** at the standard `grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo` endpoint. This means a second Bowire workbench — or any gRPC-capable client that speaks reflection — can be pointed at the mock (`bowire --url http://127.0.0.1:<port>`) and will auto-discover every mocked service without the user supplying `.proto` files out of band.

Recordings that predate Phase 1c (i.e. have no `schemaDescriptor`) still replay correctly; reflection just isn't available. The mock silently skips reflection registration when no descriptors are in play.

## What's not in Phase 1b

- **Streaming replay** &mdash; server-streaming, client-streaming, duplex. Recording-format groundwork (`timestampMs` per frame, `sentMessages` / `receivedMessages` arrays) already ships; the mock's replay side arrives in Phase 2.
- **Timed / proactive emission** &mdash; pushing MQTT messages on a schedule, broadcasting a DIS PDU sequence without a client trigger. Phase 2.
<!-- gRPC Server Reflection shipped in Phase 1c — see the dedicated section below. -->
- **Dynamic response values** &mdash; `${uuid}`, `${now}` substitution inside response bodies. Phase 2.
- **Path-template matching** &mdash; `/users/{id}` bindings. Phase 2.
- **Stateful mode**, **scenario switching**, **chaos injection**, **schema-only mocks** (without a recording), **request capture on miss**. All Phase 3.

## External-client validation

The strongest correctness signal a mock can get isn't its own test suite &mdash; the test suite was written by the same hands that wrote the mock. A third-party client built for the **real** server is a much harder yardstick, because nobody bent it to pass the mock's tests.

The TacticalAPI ecosystem ships exactly such a client: [Rheinmetall's official C# test client](https://github.com/Rheinmetall/tacticalapi/tree/main/testclient/csharp). It's a small CLI built against the upstream `.proto` set, used in `TacNet` development for ad-hoc poking. Pointing it at `bowire mock` is a single line that exercises every call type the mock claims to support &mdash; unary CRUD plus server-streaming subscribe &mdash; using bytes the mock never sees from its own test fixtures.

Walkthrough &mdash; one terminal serving the mock, one running the client:

```bash
# Terminal 1 — start bowire mock against the bundled TacticalAPI descriptors.
# The proto set covers Identity, Situation, EditableSituationObjects, …
bowire mock --schema rheinmetall/tactical_api/v0/situation.proto --port 4267

# Terminal 2 — run Rheinmetall's test client against the mock.
git clone https://github.com/Rheinmetall/tacticalapi
cd tacticalapi/testclient/csharp
dotnet build TacticalApi.TestClient.csproj

# 1. Place a few symbols at WGS84 coordinates near Hamburg
dotnet TacticalApi.TestClient.dll --sendsymbol 53.5 9.9
dotnet TacticalApi.TestClient.dll --sendsymbol 53.55 10.0
dotnet TacticalApi.TestClient.dll --sendsymbol 53.6 10.05

# 2. List what's there now
dotnet TacticalApi.TestClient.dll --printsituation
# Expected: three symbols with distinct GUIDs and the lat/lon you sent

# 3. Subscribe to the live event stream
dotnet TacticalApi.TestClient.dll --observesituation
# Expected: connection opens, no error, the mock pushes the three
# existing symbols then keeps the stream alive

# 4. From another terminal, run --sendsymbol again. The
#    --observesituation stream should emit a new event for it.
```

Each step crosses a different mock surface: stateful storage (sendsymbol persists for printsituation), server-streaming replay (observesituation), GUID generation (sendsymbol allocates), and &mdash; if the test client is configured for port 4268 instead of 4267 &mdash; **gRPC-Web** framing on top of HTTP/1.1, which a wire-level bug would expose immediately.

The point of the exercise isn't that the mock is "correct" once these commands pass &mdash; the test client also has its own assumptions baked in. It's that an *independent third party's* assumptions about the protocol are met. When they aren't, you have a concrete, reproducible delta that's easy to fix on whichever side is wrong.

**Bowire's [TacticalAPI plugin docs](../protocols/tacticalapi.md#try-it--upstream-test-client-as-data-populator)** include a companion walkthrough that points the same test client at a real Rheinmetall server instead &mdash; useful for the inverse comparison ("does Bowire reproduce what the real server does?").

## See also

- [Roadmap: Replay-Mock-Server](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#replay-mock-server--recordings-as-api-endpoints) &mdash; the three-phase plan.
- [Recorder](recording.md) &mdash; how to produce the recording files the mock replays.
- [CLI Mode](cli-mode.md) &mdash; the parent `bowire` command, of which `bowire mock` is one subcommand.
- [TacticalAPI plugin](../protocols/tacticalapi.md) &mdash; the sibling plugin that ships the proto descriptors used in the walkthrough above.

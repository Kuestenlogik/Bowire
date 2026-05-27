---
summary: 'In-tree NATS plugin on the official NATS.Net 2.x client. Three discovery sources from one shared connection: subject sampling, JetStream streams, Services API.'
---

# NATS Protocol

Bowire's NATS plugin connects to any `nats://` server over the official **NATS.Net 2.x** client and discovers the surface from **three sources** running on the same connection: passive subject sampling on the `>` wildcard, JetStream stream enumeration (when `-js` is on), and the Services API (`$SRV.PING` broadcast).

## Setup

In-tree plugin — no separate install needed.

### Standalone

```bash
bowire --url nats://localhost:4222
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("nats://localhost:4222");
});
```

## URL shapes

```
nats://host:4222          # core
tls://host:4443           # TLS
ws://host:8080            # WebSocket transport
wss://host:8443           # WebSocket over TLS
host[:port]               # bare; scheme defaults to nats://
```

## Discovery

Three sources, each best-effort (a failure in one doesn't kill the others):

1. **Subject sampling** — subscribe to `>` for a short window (default 3 s, configurable via the `scanDuration` setting), group observed subjects by first-token prefix into services. Each subject becomes three methods:
   - **Publish** (Unary)
   - **Subscribe** (ServerStreaming) — accepts a `queue_group` metadata key for NATS-side load-balanced fan-out
   - **Request** (Unary) — req/reply with inbox replies
2. **JetStream streams** — `INatsJSContext.ListStreamsAsync` enumerates streams; each becomes a `JetStream:<name>` service with `info` (Unary), `consume` (ServerStreaming via ordered consumer), and one JS-acked `publish` per filtered subject.
3. **Services API** — `$SRV.PING` broadcast collects responding service names; a follow-up `$SRV.INFO.<name>` request parses the endpoint list and exposes each endpoint as a req/reply method.

## Payload Handling

Same JSON → UTF-8 → hex fallback chain Bowire uses for MQTT.

## Settings

- `autoInterpretJson` (bool, default `true`) — parse JSON payloads for structured display
- `scanDuration` (number, default `3`) — subject-scan window in seconds

## Sample

A docker-compose NATS broker (with JetStream) lives at [`samples/Nats`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Nats) — `docker compose up`, point Bowire at `nats://localhost:4222`.

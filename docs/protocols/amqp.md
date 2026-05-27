---
summary: 'AMQP 0.9.1 + 1.0 in one sibling plugin. RabbitMQ via RabbitMQ.Client, ActiveMQ / Solace / Azure Service Bus via AMQPNetLite. Wire picked by URL scheme.'
---

# AMQP Protocol

`Kuestenlogik.Bowire.Protocol.Amqp` (sibling repo) covers **both** major AMQP wires in one plugin id — the actual wire is picked from the URL scheme:

| URL scheme | Wire | Library | Discovery |
|------------|------|---------|-----------|
| `amqp://` / `amqps://` | AMQP **0.9.1** | RabbitMQ.Client | RabbitMQ Management HTTP API |
| `amqp1://` / `amqps1://` | AMQP **1.0** | AMQPNetLite | Synthetic `Broker` service |

**Package:** `Kuestenlogik.Bowire.Protocol.Amqp` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Amqp
```

### Standalone — AMQP 0.9.1 (RabbitMQ)

```bash
bowire --url amqp://localhost:5672
```

### Standalone — AMQP 1.0 (ActiveMQ / Solace / Azure SB)

```bash
bowire --url amqp1://localhost:5672
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("amqp://localhost:5672");
});
```

## Discovery

**AMQP 0.9.1** — `AmqpDiscovery` hits the RabbitMQ Management HTTP API (`/api/queues`, `/api/exchanges`) on port 15672 by default. Each queue surfaces as a service with `publish` (Unary) and `consume` (ServerStreaming); each exchange surfaces as a service with `publish` per binding key.

**AMQP 1.0** — surfaces a synthetic `Broker` service with generic `send` + `receive` methods. AMQP 1.0 doesn't have a standard discovery mechanism; broker-specific addressing rides on the metadata bag.

## Invocation

Consume returns a structured envelope per message:

```json
{
  "exchange": "...",
  "routingKey": "...",
  "contentType": "application/json",
  "messageId": "...",
  "encoding": "...",
  "payload": "..."
}
```

## Security

Both wires honour the shared `__bowireMtls__` + `__bowireAmqpSasl__` marker keys for client cert + SASL auth respectively.

## Settings

- `managementApiPort` (number, default `15672`) — RabbitMQ Management API port; per-URL override via `?_mgmtPort=…`
- `discoveryTimeoutSeconds` (number, default `5`)
- `receiveTimeoutSeconds` (number, default `30`)

## Mock replay

`AmqpMockEmitter` re-publishes recorded messages on a `bowire mock` server. Recordings with `protocol: "amqp"` step kind get routed here, honouring loop / replay-speed.

## Coverage

First sibling plugin to clear stable. Live Testcontainers RabbitMQ integration suite under `[Trait("Category","Docker")]` covers both the protocol and the mock-emitter publish loop. Line coverage 79.5% (Mock emitter + security config 100%).

See: [Recording](../features/recording.md), [Mock Server](../features/mock-server.md).

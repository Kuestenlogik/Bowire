---
summary: 'The Surgewave plugin browses topics and produces / consumes messages against a Küstenlogik Surgewave broker over the native Surgewave wire or a Kafka-compatible wire on the same broker.'
---

# Surgewave

The Surgewave plugin browses topics and produces / consumes messages against a [Küstenlogik Surgewave](https://github.com/Kuestenlogik/Surgewave) broker. Surgewave's broker speaks both the native Surgewave wire and the Kafka wire — the plugin lets you pick which protocol it uses against the same broker, including Confluent Schema Registry decode in Kafka mode.

**Package:** `Kuestenlogik.Bowire.Protocol.Storm` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Storm
```

Then point Bowire at a `storm://` URL.

### Standalone

```bash
bowire --url storm://broker.example:9092
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("storm://broker.example:9092");
});
```

In-process testing has a `storm://embedded` URL that taps the host's broker without going through the network.

## URL shapes

```
storm://broker.example:9092                                            # single broker, auto-detect protocol
storm://b1:9092,b2:9092                                                # bootstrap servers CSV
broker.example:9094                                                    # bare host:port also accepted
storm://broker:9092?protocol=storm                                     # force the native Surgewave wire
storm://broker:9092?protocol=kafka                                     # force the Kafka-compatible wire
storm://broker:9092?protocol=kafka&schema-registry=http://sr:8081      # Kafka-mode + Confluent Schema Registry
storm://embedded                                                       # in-process broker tap
```

## Protocol modes

The `?protocol=` query parameter picks how `StormClientBuilder` configures the wire:

| Value                              | Effect |
|------------------------------------|--------|
| `storm` / `native` / `storm-native` | `UseStormProtocol()` — max performance, advanced features (SharedMemory transport, batching presets, handler dispatch) |
| `kafka`                             | `UseKafkaProtocol()` — interoperable wire, Confluent Schema Registry support |
| `auto` (default when omitted)       | `UseAutoDetect()` — try native first, fall back to Kafka |

Unknown values fall back to `auto`, so a typo never breaks discovery.

## Discovery

Connects via `StormClient.Create(...).BuildAsync()` and surfaces a `Cluster` service with broker metadata. Topic enumeration on the native Surgewave protocol is pending an admin/metadata API on the client SDK — once that lands, topics populate the sidebar the same way the [Kafka plugin](kafka.md) already does via `IAdminClient.GetMetadata`. Until then, type the topic name into the workbench's method dropdown.

## Methods

Per topic, identical shape to [`Bowire.Protocol.Kafka`](kafka.md):

| Method    | Kind            | Description |
|-----------|-----------------|-------------|
| `consume` | ServerStreaming | Subscribe + yield one JSON envelope per message (topic, partition, offset, timestamp, key/keyBase64, value/valueBase64, bytes) |
| `produce` | Unary           | Publish one message; optional `key` and `partition` via metadata |

The envelope shape matches [`Bowire.Protocol.Kafka`](kafka.md) byte-for-byte, so a recording captured against one plugin replays against the other — swap the step's `protocol` string from `"kafka"` to `"storm"` (or vice versa).

## Schema Registry (Kafka mode)

When the URL carries `?schema-registry=…` and the consumed message is framed in the Confluent wire format (`0x00` magic byte + 4-byte big-endian schema id + Avro/JSON body), the plugin decodes it on the fly. The envelope's `value` field then carries the JSON projection and an additional `encoding` field tags the schema kind (`"avro"`, …). Schemaless topics, plain UTF-8, and opaque binary keep the original fallback path.

The wire-format decoder is duplicated from [`Bowire.Protocol.Kafka`](kafka.md) (same `KafkaSchemaRegistry` + `AvroValueToJson` types). Two consumers don't justify a third NuGet package — if a third Kafka-wire plugin shows up, the natural refactor is to lift them into a shared library.

## Security

Surgewave reuses the same auth markers as the [Kafka plugin](kafka.md) — see [Authentication](../features/authentication.md):

- `__bowireMtls__` for client-cert + CA bundle (PEM strings, no temp files)
- `__bowireKafkaSasl__` for SASL/PLAIN, SCRAM-SHA-256/512, OAUTHBEARER

Both markers are stripped from the metadata dict before it's forwarded as message headers, so secrets never reach the wire.

## Mock replay

`StormMockEmitter` plugs into Bowire's mock server via `IBowireMockEmitter`. Recordings tagged `protocol: "storm"` get re-published at the original cadence:

| Metadata key (first step) | Purpose | Default |
|---------------------------|---------|---------|
| `bootstrap` (or `bootstrap-servers`) | Broker CSV | `localhost:9092` |
| Per-step `key` / `partition` | Same knobs as the live produce path | — |

Payload source: `responseBinary` (base64) wins so binary payloads round-trip byte-for-byte; text-only recordings fall back to UTF-8 of `body`.

## Relationship to the Kafka plugin

Both ship side by side and register under their own protocol id (`storm` vs `kafka`). Run them together when a single environment hits both Surgewave and Kafka brokers; recordings stay portable between the two because the consume / produce envelopes are identical.

See also: [Kafka](kafka.md), [Authentication](../features/authentication.md), [Recording](../features/recording.md).

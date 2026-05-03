---
summary: 'The Kafka plugin browses topics, consumes live traffic, produces test messages, and replays recorded streams against any Confluent.Kafka-compatible cluster — including Confluent Schema Registry decode and mTLS / SASL auth.'
---

# Kafka

The Kafka plugin browses topics, consumes live traffic, produces test messages, and replays recorded streams against any Confluent.Kafka-compatible cluster. Built on [Confluent.Kafka](https://github.com/confluentinc/confluent-kafka-dotnet).

**Package:** `Kuestenlogik.Bowire.Protocol.Kafka` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Kafka
```

### Standalone

```bash
bowire --url kafka://broker.example:9092
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("kafka://broker.example:9092");
});
```

## URL shapes

```
kafka://broker.example:9092                                   # single broker
kafka://b1:9092,b2:9092,b3:9092                               # bootstrap servers CSV
broker.example:9094                                           # bare host:port also accepted
kafka://broker:9092?schema-registry=http://sr:8081            # with Confluent Schema Registry
kafka://broker:9092?sr=http%3A%2F%2Fsr%3A8081                 # short alias + URL-encoded
```

## Discovery

A short-lived `IAdminClient` queries broker metadata. Every non-internal topic surfaces as a service with two methods:

| Method    | Kind            | Description |
|-----------|-----------------|-------------|
| `consume` | ServerStreaming | Subscribe with a throwaway group id (`bowire-<hex>`), yield one envelope per message |
| `produce` | Unary           | Publish one message; optional `key` and `partition` via metadata |

A synthetic `Cluster` service surfaces broker / topic counts so the sidebar shows "what's there" even before you pick a topic. Internal topics (`__consumer_offsets`, `_schemas`, `_confluent*`) are hidden unless the workbench's internal-services toggle is on.

## Consume envelope

Each message arrives as:

```json
{
  "topic": "orders",
  "partition": 0,
  "offset": 1234,
  "timestamp": 1745500800123,
  "key": "order-42",
  "keyBase64": "b3JkZXItNDI=",
  "value": "{\"id\":42,\"total\":19.95}",
  "valueBase64": "eyJpZCI6NDIsInRvdGFsIjoxOS45NX0=",
  "bytes": 23
}
```

`key` / `value` are the UTF-8 decode when the bytes map cleanly; `null` for binary payloads. `keyBase64` / `valueBase64` are always present so the workbench can hex-dump arbitrary payloads (Avro, Protobuf, MessagePack, …).

When the URL carries `?schema-registry=…` and the message body is framed in the Confluent wire format (`0x00` magic byte + 4-byte big-endian schema id + Avro/JSON body), the plugin decodes it on the fly. The `value` field then carries the JSON projection and an additional `encoding` field tags the schema kind (`"avro"`, …) so the UI can pick the right rendering. Plain UTF-8 / opaque binary stays in the original fallback path — the registry isn't consulted unless the framing prefix is present.

## Produce

The `produce` method publishes a single message taken from the first invocation payload:

| Metadata key | Purpose |
|--------------|---------|
| `key` | Message key (UTF-8 string) |
| `partition` | Target partition number (optional — round-robins when absent) |

## Security

Two markers feed the Kafka security knobs from Bowire's environment auth helpers — see [Authentication](../features/authentication.md):

| Marker | Source | Effect |
|--------|--------|--------|
| `__bowireMtls__` | shared mTLS auth helper across REST / gRPC / WebSocket / SignalR / Kafka | `SecurityProtocol = Ssl`, `SslCertificatePem` / `SslKeyPem` / `SslCaPem` populated from PEM strings (no temp files), optional `SslKeyPassword`. `allowSelfSigned` flips `EnableSslCertificateVerification = false`. |
| `__bowireKafkaSasl__` | Kafka-specific JSON `{ mechanism, username, password }` | `SaslMechanism = Plain` / `ScramSha256` / `ScramSha512` / `OAuthBearer`. Combined with `__bowireMtls__` → `SecurityProtocol = SaslSsl`; alone → `SaslPlaintext`. |

Both markers are stripped from the metadata dict before it's forwarded as Kafka message headers, so secrets never reach the wire.

## Mock replay

`KafkaMockEmitter` plugs into Bowire's mock server via `IBowireMockEmitter`. Recordings tagged `protocol: "kafka"` get re-published at the original cadence:

| Metadata key (first step) | Purpose | Default |
|---------------------------|---------|---------|
| `bootstrap` (or `bootstrap-servers`) | Broker CSV | `localhost:9092` |
| Per-step `key` / `partition` | Same knobs as the live produce path | — |

Payload source: `responseBinary` (base64) is preferred so Avro / Protobuf round-trips byte-for-byte; text-only recordings fall back to UTF-8 encoding of `body`.

## E2E tests

The unit-test suite is hermetic and runs anywhere `dotnet test` does. Real-broker round-trips (`KafkaRoundTripE2ETests`, `KafkaSchemaRegistryE2ETests`) sit behind the `Docker` xUnit trait — they use Testcontainers to spin up Confluent Kafka 7.7 (KRaft mode, no Zookeeper) and Schema Registry 7.7 in a shared bridge network, then drive the plugin against the live processes.

```
docker info       # confirm Docker Desktop / engine is reachable
dotnet test       # all tests, Docker ones included
dotnet test --filter "Category!=Docker"   # hermetic only
```

## Relationship to the Storm plugin

This plugin is the generic Kafka baseline — schema-registry-agnostic, serializer-agnostic, usable against any cluster. The [`Bowire.Protocol.Storm`](storm.md) plugin layers Storm-specific conventions (schema-registry URL resolution, typed payload decoders, Storm topic-naming awareness) on top for users inside the Storm ecosystem. Run either alone or install both side by side — each registers under its own protocol id (`kafka` vs `storm`).

See also: [Storm](storm.md), [Authentication](../features/authentication.md), [Recording](../features/recording.md).

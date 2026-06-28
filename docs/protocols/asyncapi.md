---
title: AsyncAPI
summary: 'AsyncAPI 2.x and 3.0 as a discovery source — Bowire reads `asyncapi.yaml` / `.json`, surfaces channels + operations + per-message overloads in the sidebar, and dispatches calls through the wire plugin the doc''s `bindings:` declare.'
---

# AsyncAPI

AsyncAPI is the OpenAPI analogue for event-driven APIs. Where OpenAPI describes HTTP request / response, AsyncAPI describes channels, operations, messages, and the transport bindings (MQTT, Kafka, AMQP, WebSocket, HTTP, NATS, …) those channels use.

In Bowire, **AsyncAPI is a discovery source, not a wire**. The plugin parses the document, builds the method tree, and dispatches each invocation through the matching wire plugin. The mental model matches `bowire --url ./openapi.yaml`: hand Bowire the contract, it builds the method list, calls go out over the right transport.

## Package

```bash
dotnet add package Kuestenlogik.Bowire.AsyncApi
```

The package id has no `.Protocol.` segment on purpose — AsyncAPI never speaks a wire itself.

## Quick start

Point Bowire at a local file:

```bash
bowire --url ./asyncapi.yaml
```

…or a remote one:

```bash
bowire --url https://api.example.com/asyncapi.yaml
```

The sidebar now shows every channel as a service, every operation as a method, every message as a typed input. Hit Send — the call routes through whichever wire plugin the doc's `bindings:` block declares.

## Active bindings

Today every active binding key resolves to a shipped, first- or third-party plugin:

| `bindings:` key | Dispatched via | Channel address &rarr; |
|---|---|---|
| `http` | Built-in `HttpClient` (no extra wire needed) | URL path + verb |
| `mqtt` / `mqtt5` | [`Kuestenlogik.Bowire.Protocol.Mqtt`](mqtt.md) | Topic. QoS / retain / will fields ride on the metadata bag. |
| `kafka` | [`Kuestenlogik.Bowire.Protocol.Kafka`](kafka.md) | Topic. Schema-Registry hints + key / partition on metadata. |
| `amqp` (0.9.1) | [`Kuestenlogik.Bowire.Protocol.Amqp`](https://github.com/Kuestenlogik/Bowire.Protocol.Amqp) | Exchange / routing key. |
| `amqp1` (1.0) | [`Kuestenlogik.Bowire.Protocol.Amqp`](https://github.com/Kuestenlogik/Bowire.Protocol.Amqp) | Address. |
| `ws` | [`Kuestenlogik.Bowire.Protocol.WebSocket`](websocket.md) | Channel address as URL path. |

NATS follows once `Kuestenlogik.Bowire.Protocol.Nats` ships.

## Spec coverage

* **AsyncAPI 2.x and 3.0** — both YAML and JSON.
* **`$ref` resolution** — local + remote; `components.messages` / `components.schemas` are expanded inline.
* **Multi-server documents** — `servers[]` becomes multiple Bowire targets in one discovery pass; the same shape as `--url X --url Y`.
* **Operation polarity** — AsyncAPI tags `send` / `receive` from the application's perspective; Bowire is the test client, so the polarity inverts once in the mapping layer rather than per binding.
* **Per-message overloads** — operations declaring multiple `messages[]` emit one method per message, named `opKey::messageName`.

## Architecture

```
+-------------------+
|  asyncapi.yaml    |
|  asyncapi.json    |
+-------------------+
          |
          v
+-------------------+        +-----------------------+
|  AsyncAPI plugin  |  -->   |  BindingResolver      |
|  (channels +      |        |  per bindings.* key   |
|   operations)     |        +-----------------------+
+-------------------+                  |
                                       v
              +------------+   +------------+   +------------+   +------------+   +------------+
              | Protocol.  |   | Protocol.  |   | Protocol.  |   | Protocol.  |   |  built-in  |
              | Mqtt       |   | Kafka      |   | Amqp       |   | WebSocket  |   |  HTTP      |
              +------------+   +------------+   +------------+   +------------+   +------------+
```

The benefit: an AsyncAPI doc travels as the contract surface (review it in PRs, version it in Git), but the runtime is whatever wire the broker speaks. Switch from Kafka to MQTT by editing the `bindings:` block; no Bowire-side changes needed.

## Building blocks

Built on the official AsyncAPI .NET SDK ([`Neuroglia.AsyncApi.Core`](https://github.com/asyncapi/net-sdk) + `.IO`). The `Client.Bindings.*` packages are *not* used — they ship their own MQTT / Kafka clients that would duplicate Bowire's existing wire plugins. Neuroglia is the schema reader; wire calls go through `BowireProtocolRegistry` at runtime.

## Schema export (planned)

Inverse of the loader: emit an AsyncAPI 3.0 document from the discovered topics / methods of running MQTT / Kafka / WebSocket targets. Mirrors the planned OpenAPI export from REST discovery. Tracked in the [roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#asyncapi-as-a-discovery-source).

## Related

* [MQTT](mqtt.md) — wire dispatched by `bindings.mqtt` / `mqtt5`
* [Kafka](kafka.md) — wire dispatched by `bindings.kafka`
* [WebSocket](websocket.md) — wire dispatched by `bindings.ws`
* [Custom protocols](custom.md) — how to add a new `BindingResolver` for an unsupported binding key

---
summary: 'Auto-generated reference for every public type and member across the Bowire core + first-party protocol plugins.'
---

# API Reference

Auto-generated from XML doc-comments across the published assemblies. Use the sidebar (or the Search box) to find a specific type, method, or property — or browse the namespaces below.

## Core

- <xref:Kuestenlogik.Bowire> — `IBowireProtocol` contract, `BowireOptions`, `MapBowire()` extension, plugin-loading host.
- <xref:Kuestenlogik.Bowire.Auth> — cookie jar, mTLS handler, AWS Sig v4 signer.
- <xref:Kuestenlogik.Bowire.Mocking> — recording / step / mock-emitter contracts shared across plugins.
- <xref:Kuestenlogik.Bowire.Models> — service / method / message / field info DTOs the dispatcher sees.
- <xref:Kuestenlogik.Bowire.PluginLoading> — `AssemblyLoadContext` plumbing for `bowire plugin install`.

## First-party protocol plugins

- <xref:Kuestenlogik.Bowire.Protocol.Grpc> — includes **gRPC-Web support** via `GrpcTransportMode` and `BowireGrpcProtocol.TransportMetadataKey`; see the [gRPC-Web transport](../protocols/grpc.md#grpc-web-transport) section in the protocol guide.
- <xref:Kuestenlogik.Bowire.Protocol.Rest>
- <xref:Kuestenlogik.Bowire.Protocol.GraphQL>
- <xref:Kuestenlogik.Bowire.Protocol.SignalR>
- <xref:Kuestenlogik.Bowire.Protocol.WebSocket>
- <xref:Kuestenlogik.Bowire.Protocol.Sse>
- <xref:Kuestenlogik.Bowire.Protocol.Mqtt>
- <xref:Kuestenlogik.Bowire.Protocol.Nats>
- <xref:Kuestenlogik.Bowire.Protocol.Soap>
- <xref:Kuestenlogik.Bowire.Protocol.JsonRpc>
- <xref:Kuestenlogik.Bowire.Protocol.Pulsar>
- <xref:Kuestenlogik.Bowire.Protocol.SocketIo>
- <xref:Kuestenlogik.Bowire.Protocol.OData>
- <xref:Kuestenlogik.Bowire.Protocol.Mcp>

## Polyglot sidecar plugins

Bowire also accepts non-.NET plugins via a JSON-RPC 2.0 sidecar bridge (stdio or HTTP/SSE) — no .NET assembly, no `IBowireProtocol` implementation needed. The wire contract, manifest schema, and packaging / install flow (zip, `http(s)://`, `oci://`) are documented in [Sidecar Plugins](../architecture/sidecar-plugins.md). The Python SDK lives in its own repo: [`Kuestenlogik/Bowire.Sdk.Python`](https://github.com/Kuestenlogik/Bowire.Sdk.Python) (`pip install bowire-plugin`).

## Sibling plugins

These ship from their own NuGet packages with independent release cadences — see the [Protocol Guides](../protocols/index.md) for install snippets.

- `Kuestenlogik.Bowire.Protocol.Surgewave`
- `Kuestenlogik.Bowire.Protocol.Kafka`
- `Kuestenlogik.Bowire.Protocol.Amqp`
- `Kuestenlogik.Bowire.Protocol.Dis`
- `Kuestenlogik.Bowire.Protocol.Udp`
- `Kuestenlogik.Bowire.Protocol.Akka`

> **TacticalAPI (preview)** — `Kuestenlogik.Bowire.Protocol.TacticalApi` ships from <https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi> with its own release cadence; its API surface is not part of this DocFX scope. See the [TacticalAPI protocol guide](../protocols/tacticalapi.md) for install + usage, and the sibling repo's README for the proto-fetch licensing rationale.

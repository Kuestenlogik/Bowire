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

- <xref:Kuestenlogik.Bowire.Protocol.Grpc>
- <xref:Kuestenlogik.Bowire.Protocol.Rest>
- <xref:Kuestenlogik.Bowire.Protocol.GraphQL>
- <xref:Kuestenlogik.Bowire.Protocol.SignalR>
- <xref:Kuestenlogik.Bowire.Protocol.WebSocket>
- <xref:Kuestenlogik.Bowire.Protocol.Sse>
- <xref:Kuestenlogik.Bowire.Protocol.Mqtt>
- <xref:Kuestenlogik.Bowire.Protocol.SocketIo>
- <xref:Kuestenlogik.Bowire.Protocol.OData>
- <xref:Kuestenlogik.Bowire.Protocol.Mcp>

## Sibling plugins

These ship from their own NuGet packages with independent release cadences — see the [Protocol Guides](../protocols/index.md) for install snippets.

- `Kuestenlogik.Bowire.Protocol.Surgewave`
- `Kuestenlogik.Bowire.Protocol.Kafka`
- `Kuestenlogik.Bowire.Protocol.Dis`
- `Kuestenlogik.Bowire.Protocol.Udp`
- `Kuestenlogik.Bowire.Protocol.Akka`

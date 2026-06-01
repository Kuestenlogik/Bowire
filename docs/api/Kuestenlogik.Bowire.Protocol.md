---
uid: Kuestenlogik.Bowire.Protocol
title: Kuestenlogik.Bowire.Protocol namespace
summary: 'Sub-namespace stub — protocol-plugin assemblies live under Kuestenlogik.Bowire.Protocol.<Name>. Lets the auto-generated breadcrumb resolve cleanly.'
---

# `Kuestenlogik.Bowire.Protocol` namespace

The `Kuestenlogik.Bowire.Protocol` namespace acts purely as a parent for the first-party protocol-plugin assemblies. Each plugin lives under its own sub-namespace and ships from its own NuGet package — no public types live directly here, so DocFX wouldn't auto-generate a page for it; this stub plugs the gap that the breadcrumb on every protocol-plugin type page links into.

## Protocol plugins (each in its own NuGet package)

- `Kuestenlogik.Bowire.Protocol.Grpc`
- `Kuestenlogik.Bowire.Protocol.Rest`
- `Kuestenlogik.Bowire.Protocol.GraphQL`
- `Kuestenlogik.Bowire.Protocol.SignalR`
- `Kuestenlogik.Bowire.Protocol.WebSocket`
- `Kuestenlogik.Bowire.Protocol.Sse`
- `Kuestenlogik.Bowire.Protocol.Mqtt`
- `Kuestenlogik.Bowire.Protocol.SocketIo`
- `Kuestenlogik.Bowire.Protocol.OData`
- `Kuestenlogik.Bowire.Protocol.Mcp`

## See also

- [Protocols guide](../protocols/index.md) — behaviour, conventions, and setup notes per protocol (the human-readable counterpart to this API reference).
- [API reference index](index.html) — full namespace + assembly map.

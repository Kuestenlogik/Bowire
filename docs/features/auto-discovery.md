---
title: Auto-discovery
summary: 'Bowire automatically discovers available services and methods without manual configuration.'
---

# Auto-Discovery

Bowire automatically discovers available services and methods without manual configuration. Each protocol plugin implements its own discovery mechanism.

## How It Works

When the Bowire UI loads, it calls the `/bowire/api/services` endpoint. This triggers each registered protocol plugin's `DiscoverAsync` method, which scans for available services.

- **gRPC** -- queries gRPC Server Reflection to enumerate services, methods, and protobuf schemas
- **SignalR** -- scans the application's `EndpointDataSource` for mapped hubs with `HubMetadata`, then reflects hub methods to determine parameter types, return types, and streaming direction
- **SSE** -- finds endpoints marked with `[SseEndpoint]`, endpoints producing `text/event-stream`, or manually registered via `AddBowireSseEndpoint()`

## What Gets Discovered

For each service, Bowire discovers:

- **Service name** -- fully qualified (e.g., `weather.WeatherService` for gRPC, `ChatHub` for SignalR)
- **Methods** -- all callable methods with their call type (unary, server streaming, client streaming, duplex)
- **Input schema** -- field names, types, and nesting for request messages
- **Output schema** -- field names and types for response messages
- **Protocol badge** -- which protocol the service belongs to

## Filtering Internal Services

By default, internal services like `grpc.reflection.v1alpha.ServerReflection` are hidden. Enable them with:

```csharp
app.MapBowire(options =>
{
    options.ShowInternalServices = true;
});
```

## When discovery finds nothing

An empty sidebar has several very different causes, and until v2.3 they all
looked the same. `/bowire/api/services` now answers with an RFC 7807
`application/problem+json` body whose `attempts` array accounts for
**every** plugin that got a turn — including the ones that ran cleanly and
found nothing, which is by far the most common case and the one the old
error-only list hid completely.

```jsonc
{
  "type": "urn:bowire:discovery:no-match",
  "title": "No protocol plugin recognised this URL",
  "status": 502,
  "serverUrl": "https://api.example.com",
  "attempts": [
    { "pluginId": "grpc",  "plugin": "gRPC",    "outcome": "error",   "servicesFound": 0, "durationMs": 2011, "message": "connection refused" },
    { "pluginId": "mqtt",  "plugin": "MQTT",    "outcome": "timeout", "servicesFound": 0, "durationMs": 8003, "message": "probe exceeded the 8 s ceiling" },
    { "pluginId": "rest",  "plugin": "REST",    "outcome": "empty",   "servicesFound": 0, "durationMs": 142,  "message": "returned no services" }
  ],
  "hint": "Add a `protocol@` prefix (e.g. `rest@https://api.example.com`) to pin a specific plugin and skip the others' probes."
}
```

Each attempt carries:

| Field | Meaning |
| --- | --- |
| `pluginId` | The plugin's `IBowireProtocol.Id` — the stable machine key (`grpc`, `rest`, …) |
| `plugin` | The plugin's display name |
| `outcome` | `ok` \| `empty` \| `error` \| `timeout` |
| `servicesFound` | Services this plugin contributed |
| `durationMs` | Wall-clock cost of that one probe |
| `message` | One-liner: the service count, `returned no services`, the raw exception text, or the ceiling that was hit |

Three `type` URNs split the triage:

- `urn:bowire:discovery:no-match` — plugins ran; read `attempts` to see who failed and who simply didn't recognise the URL.
- `urn:bowire:discovery:no-plugins` — the registry is empty. `attempts` is present but empty.
- `urn:bowire:discovery:unknown-plugin` — a `protocol@` hint named a plugin this host doesn't have. `plugins` lists the ids it does.

In the workbench the same data renders as a disclosure: collapsed to
`12 plugins probed · 3 failed` on the discovery-failed card, the per-URL
status rows and the topbar connection popover, and always expanded under
**Discovery diagnostics** in the Sources detail pane, which also has a
**Copy diagnostics** button for bug reports.

On the terminal, [`bowire discover`](cli-mode.md) prints the same table
from the same code path, so the UI and the CLI can never disagree about
what happened.

## Requirements

- **gRPC**: `AddGrpcReflection()` and `MapGrpcReflectionService()` must be configured
- **SignalR**: hubs must be mapped with `MapHub<T>()` before `MapBowire()`
- **SSE**: endpoints must be annotated or registered before `MapBowire()`

See also: [Protocols](../protocols/index.md) for protocol-specific discovery details,
and [Service catalogue](catalogue.md) for where the URLs being discovered come from
when you don't want to type them.

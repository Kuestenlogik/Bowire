---
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

## Requirements

- **gRPC**: `AddGrpcReflection()` and `MapGrpcReflectionService()` must be configured
- **SignalR**: hubs must be mapped with `MapHub<T>()` before `MapBowire()`
- **SSE**: endpoints must be annotated or registered before `MapBowire()`

See also: [Protocols](../protocols/index.md) for protocol-specific discovery details.

---
summary: 'The SSE plugin discovers and subscribes to Server-Sent Events endpoints in your ASP.NET application.'
---

# SSE Protocol

The SSE plugin discovers and subscribes to Server-Sent Events endpoints in your ASP.NET application.

**Package:** `Kuestenlogik.Bowire.Protocol.Sse`

## Setup

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Sse
```

### Using the Attribute

Mark your SSE endpoints with `[SseEndpoint]`:

```csharp
app.MapGet("/events/ticker", [SseEndpoint(Description = "Live price updates", EventType = "price")]
async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    // ... emit SSE events
});

app.MapBowire();
```

### Using Fluent Registration

Register endpoints manually before mapping Bowire:

```csharp
app.AddBowireSseEndpoint("/events/ticker", "Ticker", "Live price updates", "price,status");
app.MapBowire();
```

### Using Produces Metadata

Endpoints with `Produces("text/event-stream")` metadata are also discovered automatically.

## Discovery

The SSE plugin finds endpoints through three mechanisms:

1. **`[SseEndpoint]` attribute** -- endpoints decorated with the attribute
2. **`Produces("text/event-stream")` metadata** -- endpoints with SSE content type
3. **Manual registration** -- endpoints added via `AddBowireSseEndpoint()`

In embedded mode, the plugin scans `EndpointDataSource` for matching endpoints.

## Streaming

SSE is streaming-only -- there is no request/response pattern. When you select an SSE endpoint in Bowire and click **Subscribe**, the plugin connects to the endpoint and streams events to the UI.

Each SSE event is parsed according to the standard format:

| Field | Description |
|-------|-------------|
| `id` | Event identifier (for reconnection) |
| `event` | Event type name |
| `data` | Event payload |
| `retry` | Reconnection interval in milliseconds |

Events are displayed in the response viewer as JSON objects containing all parsed fields.

## Limitations

- **Server-to-client only** -- SSE is unidirectional. There is no request body or duplex support.
- **No interactive channels** -- `OpenChannelAsync` returns null for SSE endpoints.
- **Embedded mode recommended** -- endpoint discovery via attribute scanning requires access to the application's endpoint metadata.

## URL Override

You can override the target URL in the request body:

```json
{
  "url": "/events/custom-path"
}
```

If the URL starts with `http`, it is used as-is. Otherwise, it is appended to the configured server URL.

See also: [Quick Start](../setup/index.md), [Streaming](../features/streaming.md)

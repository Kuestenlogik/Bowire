---
summary: 'Embedded mode adds Bowire directly to your ASP.NET application —'
---

# Embedded Mode

Embedded mode adds Bowire directly to your ASP.NET application —
one `MapBowire()` line and you have an interactive multi-protocol
API workbench mounted at `/bowire` (configurable). This is the
recommended setup for development because every protocol plugin has
full access to the application's service provider and endpoint
metadata, so discovery is richer than what the standalone tool can
get over the network.

## How packages are organised

Bowire is split into a **core** package (`Kuestenlogik.Bowire`) and one
package per protocol (`Kuestenlogik.Bowire.Protocol.Grpc`,
`Kuestenlogik.Bowire.Protocol.SignalR`, `Kuestenlogik.Bowire.Protocol.Rest`, etc.).

**You only need to install the protocol packages you actually use.**
Each `Kuestenlogik.Bowire.Protocol.*` package transitively pulls in
`Kuestenlogik.Bowire` via its NuGet dependency graph — installing
`Kuestenlogik.Bowire.Protocol.Grpc` automatically also installs the core
package, you don't have to (and shouldn't) list both.

## gRPC

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Grpc
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

var app = builder.Build();

app.MapGrpcService<GreeterService>();
app.MapGrpcReflectionService();
app.MapBowire();

app.Run();
```

Requirements:

- `AddGrpcReflection()` and `MapGrpcReflectionService()` must be
  configured — this is what Bowire talks to during discovery
- The gRPC plugin reads `google.api.http` annotations on your
  proto methods and exposes the HTTP-transcoded endpoints alongside
  the native gRPC ones, so users can pick "via gRPC" or "via HTTP"
  per method

## SignalR

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.SignalR
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

var app = builder.Build();
app.MapHub<ChatHub>("/chathub");
app.MapBowire();

app.Run();
```

Requirements:

- Hubs must be mapped with `MapHub<T>()` **before** `MapBowire()` —
  Bowire reads the application's endpoint metadata to discover them

## REST (OpenAPI / Swagger)

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Rest
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();   // or Swashbuckle / NSwag

var app = builder.Build();
app.MapOpenApi();                 // serves /openapi/v1.json
// ... your minimal API endpoints or controllers
app.MapBowire();

app.Run();
```

Requirements:

- An OpenAPI / Swagger document must be reachable from Bowire. The
  REST plugin probes common paths (`/openapi/v1.json`,
  `/swagger/v1/swagger.json`, etc.); if your document lives elsewhere
  you can also upload it manually via the sidebar's "Schema Files"
  tab

## GraphQL

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.GraphQL
```

```csharp
// Whichever GraphQL server you use (HotChocolate, GraphQL.NET, etc.)
app.MapGraphQL("/graphql");
app.MapBowire();
```

Requirements:

- The GraphQL endpoint must allow `__schema` introspection — most
  servers do by default in development. If introspection is disabled
  in production, upload the SDL via the sidebar's "Schema Files" tab
  as a fallback

## SSE (Server-Sent Events)

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Sse
```

```csharp
var app = builder.Build();

app.MapGet("/events/ticker", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    // ... emit SSE events
}).WithMetadata(new SseEndpointAttribute { Description = "Live ticker" });

app.MapBowire();
```

Or register endpoints explicitly via `AddBowireSseEndpoint`:

```csharp
app.AddBowireSseEndpoint("/events/ticker", "Ticker", "Live price updates");
```

## MCP (AI Agent Integration)

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Mcp
```

The MCP plugin auto-mounts the discovered unary methods as MCP tools
at `/bowire/mcp/sse` (opt-in via `--enable-mcp-adapter`). Add the
endpoint to your AI agent config — Claude Desktop, Cursor, Copilot
all support MCP servers.

## WebSocket

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.WebSocket
```

```csharp
app.UseWebSockets();
// ... your WebSocket endpoint registrations
app.MapBowire();
```

The WebSocket plugin gives you an interactive frame editor with
text + binary support, sub-protocol selection, and a per-frame
type toggle.

## All protocols at once

The plugins coexist — install the combination that matches your
service stack:

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Grpc
dotnet add package Kuestenlogik.Bowire.Protocol.SignalR
dotnet add package Kuestenlogik.Bowire.Protocol.Rest
dotnet add package Kuestenlogik.Bowire.Protocol.GraphQL
dotnet add package Kuestenlogik.Bowire.Protocol.Sse
dotnet add package Kuestenlogik.Bowire.Protocol.Mcp
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

var app = builder.Build();
app.MapGrpcService<GreeterService>();
app.MapGrpcReflectionService();
app.MapHub<ChatHub>("/chathub");
app.MapOpenApi();
app.MapGraphQL("/graphql");
app.AddBowireSseEndpoint("/events/ticker", "Ticker", "Live updates");
app.MapBowire();

app.Run();
```

## Development-only setup

Restrict Bowire to development environments:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
    app.MapBowire();
}
```

## Configuration

```csharp
app.MapBowire(options =>
{
    options.Title = "My API";
    options.Description = "v2.3 — Staging";
    options.Theme = BowireTheme.Dark;
    options.ShowInternalServices = false;
});
```

See [UI Guide](../ui-guide/index.md) for the full options reference.

## Custom route prefix

```csharp
app.MapBowire("/api-browser", options =>
{
    options.Title = "API Browser";
});
// UI is now at /api-browser instead of /bowire
```

## Reverse proxy

When running behind a reverse proxy (nginx, YARP), configure the
upstream server URL so Bowire knows where to dispatch invocations:

```csharp
app.MapBowire(options =>
{
    options.ServerUrl = "https://backend-grpc:5001";
});
```

For nginx, make sure SSE streaming isn't buffered:

```nginx
location /bowire/ {
    proxy_pass http://backend:5000;
    proxy_http_version 1.1;
    proxy_set_header Connection '';
    proxy_buffering off;
    proxy_cache off;
    chunked_transfer_encoding off;
}
```

## Securing the UI

Protect Bowire with ASP.NET authorization:

```csharp
app.MapBowire()
   .RequireAuthorization("AdminOnly");
```

## See also

- [Quick Start](../setup/index.md)
- [Standalone Tool](standalone.md)
- [Docker](docker.md)
- [Empty-State Landing](../features/empty-state.md) — what users see on their first connection

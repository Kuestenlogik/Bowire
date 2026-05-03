---
summary: 'The MCP (Model Context Protocol) plugin has two distinct sides:'
---

# MCP Protocol

The MCP (Model Context Protocol) plugin has two distinct sides:

1. **MCP Client (discovery)** — point Bowire at any MCP server URL, browse its tools / resources / prompts, and invoke them. Analogous to the gRPC and REST plugins.
2. **MCP Adapter (opt-in feature)** — expose Bowire's discovered API services as MCP tools so AI agents (Claude, Copilot, Cursor) can call your gRPC / REST / SignalR methods. **Disabled by default** to prevent silently exposing an internal API surface.

**Package:** `Kuestenlogik.Bowire.Protocol.Mcp`

## MCP Client — discovering an external MCP server

Standalone:

```bash
bowire --url http://localhost:5003/mcp
```

The URL you supply is the MCP **message endpoint** itself. The plugin uses the streamable HTTP transport: it POSTs JSON-RPC requests directly to that URL and reads the response (either `application/json` or `text/event-stream` framing). No `/sse` event stream is required.

After the handshake (`initialize` + `notifications/initialized`), Bowire calls `tools/list`, `resources/list`, and `prompts/list` and surfaces each non-empty category as its own service in the sidebar:

| Service | Methods | Invocation |
|---------|---------|------------|
| **Tools** | one per discovered tool | `tools/call` with the tool arguments |
| **Resources** | one per resource (method name = URI) | `resources/read` |
| **Prompts** | one per prompt | `prompts/get` with the prompt arguments |

Tool input schemas (`inputSchema`, JSON Schema with `type: "object"`) are mapped to the standard form-based UI: strings become text inputs, numbers become number fields, booleans become checkboxes, arrays become repeated fields, and nested objects become message fields. Required fields are marked with an asterisk.

### Sample target

`Bowire.Samples/SimpleMcp` is a hand-rolled JSON-RPC 2.0 MCP server (no Bowire / no gRPC dependency) running on port 5003. It exposes:

- **Tools** — `echo`, `add`, `weather`
- **Resources** — `notes://welcome`, `notes://changelog`
- **Prompts** — `greet` (with `name` and optional `language` arguments)

Run it and point Bowire at it:

```bash
dotnet run --project src/SimpleMcp
bowire --url http://localhost:5003/mcp
```

### Transport notes

The plugin supports the modern MCP <i>streamable HTTP</i> transport (MCP 2025-03-26): a single endpoint that accepts POSTed JSON-RPC requests and returns either `application/json` or framed `text/event-stream`. The older SSE+POST split is not supported.

## MCP Adapter — exposing Bowire as an MCP server

The adapter goes the other direction: it wraps every service Bowire can discover (gRPC, REST, SignalR) as an MCP tool. AI agents connect to Bowire as if it were an MCP server, list the tools, and invoke them — the adapter dispatches each call to the matching protocol plugin.

This is **opt-in** because it would otherwise expose any API the host happens to discover.

### Embedded mode — `WithMcpAdapter()` chained on `MapBowire()`

```csharp
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.Mcp;

app.MapBowire(options => options.Title = "My API")
   .WithMcpAdapter("https://localhost:5005");
```

The `.WithMcpAdapter()` extension only exists when the `Kuestenlogik.Bowire.Protocol.Mcp` package is referenced — projects that don't depend on it cannot accidentally activate the adapter.

### Standalone mode — `--enable-mcp-adapter` flag

```bash
bowire --url https://localhost:5005 --enable-mcp-adapter
```

Without the flag, `bowire` runs as an MCP **client** only. With it, the adapter is mapped at `/bowire/mcp/sse` + `/bowire/mcp/message`.

### Adapter endpoints

| Endpoint | Description |
|----------|-------------|
| `POST /{prefix}/mcp` | MCP <i>streamable HTTP</i> transport (MCP 2025-03-26): a single endpoint, JSON-RPC 2.0 in, JSON out. Handles `initialize`, `tools/list`, `tools/call`, `ping`. This is what `bowire --url` should target. |

The legacy SSE+POST split (`/sse` event stream + `/messages` POST) from the older MCP 2024-11-05 spec is intentionally not supported here.

### Agent configuration

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "bowire": {
      "url": "http://localhost:5080/bowire/mcp/sse"
    }
  }
}
```

Cursor uses the same `mcpServers` shape. Other MCP clients accept either the SSE URL or the message endpoint URL directly.

### Tool naming

Each discovered method becomes one tool: `{service-name-with-underscores}_{method}`. For example, `calculator.CalculatorService/Add` becomes `calculator_CalculatorService_Add`. Tool input schemas are derived from the discovered `BowireMessageInfo` (protobuf for gRPC, CLR types for REST/SignalR).

Only **unary** methods are exposed — streaming methods don't fit MCP's request/response shape.

### Security warning

Enabling the adapter lets any MCP client invoke any discovered API method. Don't use it on a server whose API surface should not be reachable from arbitrary local MCP clients. The adapter is intended for development-time AI integration, not production exposure.

### Sample

`Bowire.Samples/SimpleMcpAdapter` is a gRPC `CalculatorService` with the adapter chained in:

```csharp
app.MapBowire(options => options.Title = "Simple MCP Adapter")
   .WithMcpAdapter("https://localhost:5005");
```

Run it on port 5005 and Bowire standalone can browse it both ways:

```bash
bowire --url https://localhost:5005               # gRPC reflection
bowire --url https://localhost:5005/bowire/mcp   # MCP client
```

See also: [Quick Start](../setup/index.md), [Plugin System](../features/plugin-system.md)

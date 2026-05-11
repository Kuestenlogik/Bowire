---
summary: 'Bowire and MCP intersect in four distinct roles — pick the one that matches what you want to build.'
---

# MCP Protocol

Bowire and the **Model Context Protocol** intersect in **four distinct roles**. They live in different packages with different CLI / DI entry points; pick the one that matches what you want to build:

| Role | What it does | Package | Mount / Entry |
|------|-------------|---------|--------------|
| **1. MCP client** | Bowire connects to any MCP server URL and surfaces its tools / resources / prompts. | `Kuestenlogik.Bowire.Protocol.Mcp` | `bowire --url http://…/mcp` |
| **2. MCP adapter** | Bowire wraps every API method it can discover (gRPC, REST, SignalR, …) as an MCP tool so AI agents can call them. Opt-in. | `Kuestenlogik.Bowire.Protocol.Mcp` | `.WithMcpAdapter()` *(embedded)* or `--enable-mcp-adapter` *(standalone)* |
| **3. Bowire as MCP server — HTTP** | Bowire exposes itself (its discovery / invoke / record / mock / replay surface) as an MCP server over HTTP so AI agents can drive Bowire. | `Kuestenlogik.Bowire.Mcp` | `services.AddBowireMcp()` + `endpoints.MapBowireMcp()` |
| **4. Bowire as MCP server — stdio** | Same toolset as role 3, but spoken over stdio for agents that prefer process-level transport (Claude Desktop, Cursor stdin/stdout). | `Kuestenlogik.Bowire.Mcp` | `bowire mcp serve` |

Roles **1** and **2** are about other people's APIs (or your own gRPC/REST/SignalR APIs) flowing **through** Bowire. Roles **3** and **4** turn Bowire **itself** into something AI agents can drive — list discovered services, invoke methods, start/stop mocks, inspect recordings.

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

Without the flag, `bowire` runs as an MCP **client** only. With it, the adapter is mapped at `POST /mcp` (the workbench itself mounts at `/` in standalone, so there's no `/bowire` prefix).

### Adapter endpoints

| Endpoint | Description |
|----------|-------------|
| `POST {basePath}/mcp` | MCP <i>streamable HTTP</i> transport (MCP 2025-03-26): a single endpoint, JSON-RPC 2.0 in, JSON out. Handles `initialize`, `tools/list`, `tools/call`, `ping`. |

`{basePath}` is empty in the standalone `bowire` CLI (workbench at site root → adapter at `/mcp`) and `/bowire` (or whatever `MapBowire("/your/prefix")` you passed) in embedded mode.

The legacy SSE+POST split (`/sse` event stream + `/messages` POST) from the older MCP 2024-11-05 spec is intentionally not supported here.

### Agent configuration

Claude Desktop (`claude_desktop_config.json`), standalone CLI:

```json
{
  "mcpServers": {
    "bowire": {
      "url": "http://localhost:5080/mcp"
    }
  }
}
```

Embedded mode with the default prefix:

```json
{
  "mcpServers": {
    "your-api": {
      "url": "https://your-host/bowire/mcp"
    }
  }
}
```

Cursor uses the same `mcpServers` shape. Other MCP clients accept the message endpoint URL directly.

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
bowire --url https://localhost:5005/bowire/mcp   # MCP client (embedded sample keeps the /bowire prefix)
```

## Bowire as an MCP server — controlling Bowire from an AI agent

`Kuestenlogik.Bowire.Mcp` is a separate package that goes the other way: it exposes the Bowire **workbench itself** — discovery, invocation, recording, mocking, replay — as an MCP server so an AI agent can drive Bowire end-to-end.

This is distinct from the MCP **adapter** above: the adapter wraps the **discovered APIs** (your gRPC / REST / SignalR methods); the MCP server wraps **Bowire's own tools** (`bowire.discover`, `bowire.invoke`, `bowire.record`, `bowire.mock.start`, `bowire.recordings.list`, …). An agent uses it to ask Bowire "what's running at this URL, invoke method X with payload Y, capture the response, mock the next call, replay it".

Two transports ship in the box; pick the one your agent prefers.

### Role 3 — HTTP transport (embedded)

Mount it next to your existing Bowire endpoint:

```csharp
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Mcp;

builder.Services.AddBowire();
builder.Services.AddBowireMcp(opts =>
{
    // Without an allowlist, all server URLs are reachable. In production
    // host code you almost always want to constrain this.
    opts.AllowedServerUrls.Add("https://my-trusted-api.example.com");
});

var app = builder.Build();
app.MapBowire();      // workbench UI + REST/gRPC discovery
app.MapBowireMcp();   // MCP server for AI agents
```

`AddBowireMcp()` registers the tools and a singleton `BowireMockHandleRegistry` (for the start-mock / stop-mock tools). `MapBowireMcp()` attaches the streamable-HTTP endpoint. Default URL: `POST /mcp` next to your Bowire workbench. Allowlist enforcement lives in `BowireMcpOptions.AllowedServerUrls` — empty means "trust any URL the agent asks about", which is convenient for local dev but should be tightened in production hosts.

### Role 4 — stdio transport (`bowire mcp serve`)

For agents that prefer process-level transport (Claude Desktop's `command:` flavour, Cursor's stdio mode), invoke the CLI:

```bash
bowire mcp serve --allow-arbitrary-urls
```

Same toolset, JSON-RPC over stdin/stdout. The process exits when the agent closes the stream. `--allow-arbitrary-urls` is the stdio equivalent of leaving `AllowedServerUrls` empty.

Claude Desktop config:

```json
{
  "mcpServers": {
    "bowire": {
      "command": "bowire",
      "args": ["mcp", "serve", "--allow-arbitrary-urls"]
    }
  }
}
```

### Tool surface (roles 3 + 4)

Both transports expose the same toolset. Top-level tools include:

| Tool | Purpose |
|------|---------|
| `bowire.discover` | List services + methods at a target URL via the appropriate protocol plugin. |
| `bowire.invoke` | Call a unary method with a JSON payload. |
| `bowire.subscribe` | Sample a streaming method for a bounded window and return collected frames. |
| `bowire.env.list` / `bowire.env.get` | Read environments stored under `~/.bowire/environments.json`. |
| `bowire.recordings.list` / `bowire.recording.get` | Browse captured recordings. |
| `bowire.mock.start` / `bowire.mock.stop` / `bowire.mock.list` | Spin up an in-process mock server from a recording, stop it, list active handles. |

The full tool list is generated from the discovered `BowireMcpTools` class via the ModelContextProtocol C# SDK — call `tools/list` on the running server to see the current schemas.

### Security warning (roles 3 + 4)

The MCP server lets an agent drive any URL that's allowlisted (or any URL at all if no allowlist is configured). Treat it the same way you'd treat a CLI with shell access: only run it against trusted target systems, and prefer the `AllowedServerUrls` allowlist for non-localhost production hosts.

See also: [Quick Start](../setup/index.md), [Plugin System](../features/plugin-system.md)

---
title: MCP
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
| **Tools** | one per discovered tool | `tools/call` with the tool arguments — server-streaming: what the server reports while the tool runs, then the result |
| **Resources** | one per resource (method name = URI) | `resources/read` |
| **Prompts** | one per prompt | `prompts/get` with the prompt arguments |
| **Tasks** | `get`, `follow`, `update`, `cancel` — only on a server that runs tasks | `tasks/get`, `tasks/update`, `tasks/cancel` by `taskId`; see [Tasks](#tasks--long-running-tool-calls) |

Tool input schemas (`inputSchema`, JSON Schema with `type: "object"`) are mapped to the standard form-based UI: strings become text inputs, numbers become number fields, booleans become checkboxes, arrays become repeated fields, and nested objects become message fields. Required fields are marked with an asterisk.

### Progress and log messages during a tool call

A tool call is one request, but the server can say things while it runs. Bowire shows them live: each tool is a server-streaming method, and the stream carries one frame per message, in the order the server sends them, with the tool's result as the last frame:

```json
{ "event": "progress", "data": { "progress": 2, "total": 3 } }
{ "event": "log",      "data": { "level": "info", "data": "step 2" } }
{ "event": "result",   "data": { "content": [ { "type": "text", "text": "counted 3" } ] } }
```

- **`progress`** — `notifications/progress` for this call; Bowire sends a progress token with every `tools/call`, so a server that reports progress can.
- **`log`** — `notifications/message`. MCP deprecated logging in spec 2026-07-28 (SEP-2577), but servers built on earlier revisions still send it, so Bowire listens — and asks for it (`logging/setLevel`) only when the server declares the `logging` capability.
- **`result`** — the `tools/call` result exactly as the server returned it; a tool's own failure is a result with `isError: true`. **`error`** means the call itself could not be made.

A tool that says nothing is a stream of one frame, its result. Resources and prompts have no progress and answer with a single frame too.

### Tasks — long-running tool calls

A server can answer a `tools/call` with a durable **task** instead of a result: a handle the client follows until the work is done, answers when the server needs input, and can cancel. Bowire speaks both shapes in use:

| Shape | Where it comes from | How Bowire knows |
|---|---|---|
| **2026-07-28** | the `io.modelcontextprotocol/tasks` extension (SEP-2663) | the server lists the extension under `capabilities.extensions` in its `server/discover` answer |
| **2025-11-25** | tasks as the experimental part of the 2025-11-25 core spec | the session negotiated 2025-11-25 and a tool declares `execution.taskSupport: "optional"` or `"required"` |

On such a server, discovery adds a **Tasks** service, and the Tools service says that calls may run as tasks; on a 2025-11-25 server each task-capable tool says so in its description. A server with neither is called exactly as before.

**Calling a tool.** With the extension, Bowire declares it on every `tools/call` (`_meta["io.modelcontextprotocol/clientCapabilities"].extensions`), and the server decides per call whether to answer with a result or a task. Under 2025-11-25 Bowire adds `task: {}` to the call for the tools marked `optional` or `required` — Bowire is the place to watch one run. A result is the one `result` frame, as without tasks. A task streams its status instead:

```json
{ "event": "task",   "data": { "shape": "2026-07-28", "taskId": "786512e2…", "status": "working", "via": "create",
                               "createdAt": "2026-10-20T10:30:00Z", "ttlMs": 3600000, "pollIntervalMs": 5000 } }
{ "event": "task",   "data": { "status": "input_required", "via": "poll", "inputRequests": { "name": { "method": "elicitation/create", … } }, … } }
{ "event": "task",   "data": { "status": "completed", "via": "poll", … } }
{ "event": "result", "data": { "content": [ { "type": "text", "text": "hello Ada" } ] } }
```

- A **`task`** frame comes whenever the server reports something new: status, status message, input requests, TTL or poll interval. `via` says how Bowire learned it — `create`, `poll` or `notification`.
- The stream ends with the **`result`** of a `completed` task; with an **`error`** frame for a `failed` one (the JSON-RPC error the server recorded); or with the `cancelled` task frame itself.
- **Polling** honours the server's `pollIntervalMs`. Under the extension Bowire also sends `subscriptions/listen` for the task id; when the server acknowledges that id, its `notifications/tasks` replace polling until the subscription stream ends. Under 2025-11-25, `notifications/tasks/status` are shown when they arrive, but polling carries on, as that spec asks.
- **TTL.** Past `createdAt + ttlMs` a task that has not ended may be considered gone; Bowire stops following it and says so. A server that already purged a task answers `-32602`, which Bowire reports as "expired or never existed" — the two look the same from the client.
- **Stopping the stream stops following.** It does not cancel the task: the server keeps working on it, and `Tasks/follow` picks it up again.

**In the workbench.** A tool call that becomes a task shows a task card above the stream: status, the server's status message, the spec shape, the task id, how long it has been running, the TTL left, and how the last update arrived. While the task waits for input, the card renders a form per elicitation request — **Accept** and **Decline** send it with `Tasks/update`, and a key already answered is not offered again. **Cancel task** sends `Tasks/cancel` and shows the server's answer; **Stop** only stops following. A task still running when the page is reloaded is listed in the tool's response pane with **Resume following**, which opens `Tasks/follow` for it.

**The Tasks service.** Each method takes the `taskId` from a task frame, so the CLI, REST and the MCP adapter reach a task the same way the workbench does:

| Method | Request | Answer |
|---|---|---|
| `get` | `{ "taskId": "…" }` | the task's state, with `result` or `error` once it ended |
| `follow` | `{ "taskId": "…" }` | a stream like the tool call's, from the task's current state on — what the workbench uses to resume after a reload |
| `update` | `{ "taskId": "…", "inputResponses": { "<key>": { "action": "accept", "content": { … } } } }` | an acknowledgement; keys match the task's `inputRequests` |
| `cancel` | `{ "taskId": "…" }` | an acknowledgement, plus the task's status right after |

Cancellation is **cooperative**: the server is asked to stop, and the task may still complete or fail before it does. The answer to `cancel` says so and shows the status the server reported right after the request; the followed stream shows how it actually ended.

A unary invoke of a tool (`bowire call`, REST) follows the task itself and answers with its result. When the task needs input, the call ends with a message naming the `taskId` — answer it with `Tasks/update` and pick it up with `Tasks/follow`.

**Limits of this release.**

- Input requests of a **2025-11-25** task are not answered: in that shape they arrive as `elicitation/create` requests while the client blocks on `tasks/result`, not through `tasks/update`. The `input_required` status still shows, and `Tasks/update` says why it cannot help.
- **Sampling** requests (`sampling/createMessage`) inside `inputRequests` are shown but not answered.
- A 2025-11-25 server that binds tasks to its session may refuse `Tasks/get`, `follow` or `cancel` from a new session; following the tool call's own stream is not affected.
- Bowire calls tools as tasks; it does not run its own MCP tools as tasks yet (#802), and tests and mocks do not assert on task lifecycles yet (#803).

### Sample target

[`samples/Kuestenlogik.Bowire.Sample.Mcp`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Mcp) is a combined sample — one project that both serves MCP over the official C# SDK's streamable-HTTP transport (message endpoint `http://localhost:5190/mcp`) **and** mounts the embedded workbench at `/bowire`. It exposes two tools:

- **Tools** — `echo` (echoes the input text) and `add` (adds two integers)
- **Task tools** — `generate_report` (works for a few seconds, then answers) and `confirm_shipment` (asks for a confirmation through an input request first). The sample runs only these two as [tasks](#tasks--long-running-tool-calls); every other tool stays synchronous.

Because it plays both roles at once, you can reach it either way:

- **Embedded** — open <http://localhost:5190/bowire>; the bundled catalogue already seeds the Sources rail with this host's own `/mcp` endpoint.
- **Separate** — point a standalone Bowire client at the same server with `bowire --url mcp@…`.

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp
bowire --url mcp@http://localhost:5190/mcp
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

### Standing the same server up over HTTP (`--bind http`)

`bowire mcp serve` speaks stdio by default. `--bind http` runs the identical
toolset behind a Kestrel of its own, for agents that connect over the network
rather than over a pipe:

```bash
bowire mcp serve --bind http --token "$MCP_SECRET"
```

The endpoint is `POST /mcp` on port 5081 — no `/bowire/` prefix, because the prefix follows the host's idea of where Bowire lives and here Bowire is the host. `--token` requires
`Authorization: Bearer <secret>` on every inbound request.

**Serve it over TLS whenever it leaves the machine.** `--token` puts a bearer
token on the wire, and a bearer token over plaintext is readable by anything
on the path. There is no Bowire flag for this — the listener takes its address
from ASP.NET's own configuration, exactly like [the workbench](../setup/standalone.md#serving-over-https):

```bash
ASPNETCORE_URLS=https://localhost:5443 bowire mcp serve --bind http --token "$MCP_SECRET"
```

or `Kestrel:Endpoints` with `Kestrel:Certificates` in `appsettings.json` for a
real certificate. Leave `--port` off when you do: it is a command-line
argument, so it outranks the environment and would give you the port in
plaintext instead of the endpoint you configured. Bowire logs a line when that
happens rather than doing it quietly.

### Tool surface (roles 3 + 4)

Both transports expose the same toolset. Top-level tools include:

| Tool | Purpose |
|------|---------|
| `bowire.discover` | List services + methods at a target URL via the appropriate protocol plugin. |
| `bowire.invoke` | Call a unary method with a JSON payload. |
| `bowire.subscribe` | Sample a streaming method for a bounded window and return collected frames. |
| `bowire.env.list` / `bowire.env.get` | Read environments stored under `~/.bowire/environments.json`. |
| `bowire.network.get` | The network settings in force — proxy mode and URL, bypass list, CA bundle, which layer set each value, problems, and which protocols follow the proxy. Passwords appear only as their reference. See [Proxy and certificates](../setup/proxy-and-certificates.md). |
| `bowire.cookies.list` / `bowire.cookies.clear` | The cookie jars per environment (values masked), and clearing one jar or one domain in it (two-step). See [Authentication](../features/authentication.md#cookie-jar). |
| `bowire.recordings.list` / `bowire.recording.get` | Browse captured recordings. |
| `bowire.mock.start` / `bowire.mock.stop` / `bowire.mock.list` | Spin up an in-process mock server from a recording, stop it, list active handles. Mutators run behind a two-step confirmation gate (`--no-confirm` to disable). |
| `bowire.har.import` | Convert a HAR 1.2 trace into a Bowire recording — optionally writes it to disk for use with `bowire.mock.start`. |
| `bowire.import` | Import an Insomnia export or `.http` files into Bowire collections and environments, with a report of what was approximated, skipped or left as a clear-text credential — optionally writes a `.bww` workspace file. |
| `bowire.export.http` | Write collections from a `.bww` or `collections.json` as a `.http` file plus `http-client.env.json` for Rider, IntelliJ, Visual Studio and VS Code. Secrets are never written. |
| `bowire.assert` | Append a Newman-style assertion (`{ path, op, expected }`) onto a step inside a recording. Ops: `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains`, `matches`, `exists`, `notexists`, `type`. |
| `bowire.allowlist.show` / `bowire.allowlist.permit` | Diagnose the active URL allowlist + extend it at runtime (the latter also persists to `~/.bowire/typed-urls.json`). |
| `bowire.lint` | Discover a URL and run the design-time linter over the API surface — the same rules as `bowire lint` and the Lint rail, honouring `.bowire/rules.json`. |
| `bowire.contract.matrix` | Roll the stored contract-verification results up into the consumer × provider matrix (see [contract testing](../features/contract-testing.md)). Local-only: it reads what `bowire contract verify` stored and never contacts a provider. |
| `bowire.report.rollup` | Roll the reports Bowire writes (lint, contract, benchmark, scan, test) up into one row per service across a portfolio — see [report rollup](../features/report-rollup.md). Local-only: it reads existing files and never calls a service. |

The full tool list is generated from the discovered `BowireMcpTools` class via the ModelContextProtocol C# SDK — call `tools/list` on the running server to see the current schemas.

### Resource surface (roles 3 + 4)

Tools *do* things; resources are things an agent can read. Call `resources/list` on the running server for the live set.

| Resource | What it holds |
|----------|---------------|
| `bowire://workspaces` | Every workspace this identity has — `id`, `name`, `gitNative`, `storageRoot`. **The index the workspace-scoped resources are addressed through.** |
| `bowire://workspaces/{workspaceId}/collections` | Saved request collections in that workspace. |
| `bowire://workspaces/{workspaceId}/recordings` | Recordings captured in that workspace. |
| `bowire://workspaces/{workspaceId}/flows` | Visual flows saved in that workspace. |
| `bowire://collections`, `bowire://recordings`, `bowire://flows` | The **workspace-less** files, for a host that never adopted workspaces. |
| `bowire://collections/{id}`, `bowire://recordings/{id}`, `bowire://flows/{id}` | One item by id, from the workspace-less files. |
| `bowire://plugins` | Sibling plugins in the host's plugin directory. |

#### Why a workspace has to be named

An agent asking for `bowire://flows` sends a URI and nothing else. The workbench sends `?workspaceId=` on every call it makes, but a resource read carries no such context, and Bowire has no server-side notion of "the workspace that is currently open" — the active selection lives in the browser, deliberately, because two windows may honestly be looking at different workspaces.

So the workspace is named in the URI, and `bowire://workspaces` exists to make the ids discoverable. That index only became possible once the workspace list itself moved out of the browser and into the identity's storage slot; before that, nothing on the server knew a workspace existed.

The consequence worth knowing: **on a host that uses workspaces, the workspace-less resources are the pre-workspace files** — usually stale, often empty. They still answer, because a host that never adopted workspaces has its data exactly there and breaking that would trade one wrong answer for another. Read `bowire://workspaces` first; an empty list means the workspace-less resources are the right ones.

An unknown workspace id answers with a message naming the index rather than with an empty document. "No data" and "wrong id" are indistinguishable to whoever reads the result, and that ambiguity is what this surface previously got wrong.

### Security warning (roles 3 + 4)

The MCP server lets an agent drive any URL that's allowlisted (or any URL at all if no allowlist is configured). Treat it the same way you'd treat a CLI with shell access: only run it against trusted target systems, and prefer the `AllowedServerUrls` allowlist for non-localhost production hosts.

Three CLI flags layer the allowlist:

- *(default)* — the allowlist seeds from `~/.bowire/environments.json` (every saved environment's `serverUrl`). Add URLs explicitly via `BowireMcpOptions.AllowedServerUrls` when scripting.
- `--allow-invoke` — also seed from `~/.bowire/typed-urls.json`, which tracks every URL the user has typed into the workbench. Widens the allowlist without dropping the gate; agents can also call `bowire.allowlist.permit` to append the URL the user just typed.
- `--allow-arbitrary-urls` — drop the gate entirely. Sandboxed CI only.

`--no-confirm` disables the two-step pending-confirmation pattern on mutator tools (`bowire.mock.start`, `bowire.record.start`). Without it, the first call returns `{ pending: true, confirmationToken, plan }` and the agent must echo the token (or pass `confirm: true`) on a second call to execute. Pending tokens expire after five minutes.

See also: [Quick Start](../setup/index.md), [Plugin System](../features/plugin-system.md)

---
title: "Long-running MCP tool calls: following MCP Tasks from a client"
description: "MCP Tasks let a server answer a tool call with a durable handle instead of a result. What a client has to do to follow one — polling, pushed status, input requests, cancellation, TTLs — and the pitfalls we hit building it into Bowire."
lede: A tool call that takes ten minutes should not hold a request open for ten minutes. MCP Tasks give the server a better answer — here is what a client has to do with it.
---

Most MCP tool calls are quick: the agent asks, the server answers, done. But a growing class of tools is not — generating a report, running a migration, waiting for a human to approve something. Holding a JSON-RPC request open for that long is fragile: proxies time out, connections drop, and a client that reconnects has no way to ask "where were we?".

The **MCP Tasks** extension (`io.modelcontextprotocol/tasks`, [SEP-2663](https://github.com/modelcontextprotocol/ext-tasks), stable schema `2026-07-28`) fixes that. A server may answer a `tools/call` with a **task** — a durable handle — instead of a result. The client follows the task until it ends, answers it when it needs input, and can cancel it.

We just built task support into [Bowire](https://bowire.io)'s MCP client, so the workbench can call, follow, answer and cancel tasks on any task-capable server. This article walks through what a client has to do — and the places where the spec, the C# SDK and reality did not line up the way we expected.

## Opting in, per request

A client declares the extension **on each request**, not once per session:

```json
{
  "method": "tools/call",
  "params": {
    "name": "generate_report",
    "arguments": { "sensorId": "dock-1" },
    "_meta": {
      "io.modelcontextprotocol/clientCapabilities": {
        "extensions": { "io.modelcontextprotocol/tasks": {} }
      }
    }
  }
}
```

The server advertises support in its `server/discover` answer (`capabilities.extensions`), but the decision is the server's, per call: it may answer with an ordinary result or with a task. A server **must not** return a task to a request that did not opt in — so a client that never declares the extension behaves exactly as before.

In C#, the official `ModelContextProtocol.Extensions.Tasks` package does the opt-in for you:

```csharp
var outcome = await client.CallToolAsTaskAsync(
    new CallToolRequestParams { Name = "generate_report", Arguments = args }, ct);

if (!outcome.IsTask)
    return outcome.Result;            // the server answered right away

var taskId = outcome.TaskCreated!.TaskId;
```

One trap: if your client declares *any other* extension for the session, the SDK (2.2.0) overwrites the per-request tasks opt-in, and the call silently never becomes a task ([csharp-sdk#1920](https://github.com/modelcontextprotocol/csharp-sdk/issues/1920)). If tasks "never happen", check that first.

## What comes back

A task is a small state object:

```json
{
  "resultType": "task",
  "taskId": "786512e2-9e0d-44bd-8f29-789f320fe840",
  "status": "working",
  "createdAt": "2026-10-20T10:30:00Z",
  "lastUpdatedAt": "2026-10-20T10:30:00Z",
  "ttlMs": 600000,
  "pollIntervalMs": 500
}
```

`status` moves through `working` and `input_required` to one of three terminal states: `completed` (the result is inlined), `failed` (a JSON-RPC error is inlined) or `cancelled`. A tool that fails *as a tool* — a result with `isError: true` — is `completed`, not `failed`; `failed` is reserved for protocol-level errors.

## Following it: polling, or pushed status

The baseline is polling `tasks/get` at the interval the server asks for (`pollIntervalMs`). Each answer is the full, current task.

The extension also lets a server **push** status. The client sends `subscriptions/listen` with the task ids it cares about, and the server acknowledges with the ids it agreed to push:

```json
{ "method": "subscriptions/listen",
  "params": { "notifications": { "taskIds": ["786512e2-…"] } } }
```

Only an acknowledged id counts. Bowire treats an acknowledgement as the signal to stop polling and wait for `notifications/tasks` — and goes back to polling if the subscription stream ends. A server that does not know the method, or acknowledges without the id, leaves the client polling. Nothing is lost either way.

Two things we tripped over here:

- **The notification's name.** The spec calls it `notifications/tasks`. The C# package's constant (`TasksProtocol.NotificationTaskStatus` in 2.2.0) is `notifications/tasks/status` — the name from the older 2025-11-25 tasks. Servers built on either send one or the other, so a robust client listens for both.
- **Serialising the SDK's types.** The package's task results carry the TTL as a `TimeSpan`. Serialised through their own type info they come out as `"ttlMs": 600000`; serialised as `object` with the context's options they come out as `"ttlMs": "00:10:00"` — and the TTL is silently gone. Use the typed `JsonTypeInfo`.

## Treat the server's numbers as untrusted

`ttlMs` and `pollIntervalMs` are chosen by the server. A client that passes them straight to a timer will eventually meet one that does not fit:

- `Task.Delay` accepts at most about 49.7 days. A larger poll interval throws.
- `TimeSpan.FromMilliseconds(long)` overflows long before `long.MaxValue`, and `createdAt.AddMilliseconds(ttl)` throws for a TTL too large for a date.

Bowire caps every wait at what the timer takes (the loop re-decides after each wait, so a capped wait loses nothing), treats a TTL too large to add as unlimited, and **never waits past the TTL** — otherwise a long poll interval sleeps straight through it.

The TTL itself is a backstop the spec offers the client: past `createdAt + ttlMs`, a task that has not ended may be considered gone. And once the server has purged a task, `tasks/get` answers `-32602` — the same code as for an id that never existed. From the client, the two are indistinguishable, so say both.

## When the task needs input

A task in `input_required` carries `inputRequests` — elicitations (and possibly sampling requests) keyed by an id the server picks:

```json
"inputRequests": {
  "3696dba29e0c409c": {
    "method": "elicitation/create",
    "params": {
      "mode": "form",
      "message": "Book shipment SHP-1042?",
      "requestedSchema": {
        "type": "object",
        "properties": {
          "confirm":  { "type": "boolean", "title": "Confirm booking" },
          "quantity": { "type": "integer", "title": "Pallets", "minimum": 1, "maximum": 40 },
          "carrier":  { "type": "string", "enum": ["road", "rail", "sea"] }
        },
        "required": ["confirm", "quantity", "carrier"]
      }
    }
  }
}
```

The client answers with `tasks/update`, keyed the same way:

```json
{ "method": "tasks/update",
  "params": { "taskId": "786512e2-…",
              "inputResponses": {
                "3696dba29e0c409c": { "action": "accept", "content": { "confirm": true, "quantity": 2, "carrier": "rail" } } } } }
```

`action` is `accept`, `decline` or `cancel`, exactly as for an ordinary elicitation.

The acknowledgement is eventually consistent — keep following the task to see it continue. Keys are unique for the life of a task, and the server repeats outstanding requests on every poll, so a client should remember which keys it answered rather than prompt twice.

Server-side, the C# SDK makes this almost invisible: a tool that calls `server.ElicitAsync(…)` while running as a task turns that call into an input request on the task, and resumes when the answer arrives.

## Cancelling — and stopping

`tasks/cancel` asks the server to stop. Cancellation is **cooperative**: the task may still complete or fail before the server gets to it. Show the status right after the request, and let the followed stream tell how it actually ended.

Equally important is what is *not* a cancel: a client that stops following a task — the user closes the stream, the connection drops — has not cancelled anything. The server keeps working, and the task id is enough to pick it up again. That is the whole point of a durable handle, and it is why Bowire keeps the ids of running tasks across a reload.

## The older shape is still out there

Tasks first appeared, experimentally, in the **2025-11-25** core spec, and servers built on it are in use. The shapes differ in the details: a tool declares `execution.taskSupport`, the client adds `task: {}` to `tools/call`, the answer is `{ "task": { … } }` with `ttl` and `pollInterval`, and the result is fetched with `tasks/result` once the task is terminal. Input requests are not part of the task there — they arrive as ordinary `elicitation/create` requests while the client blocks on `tasks/result`.

A client that wants to work with both has to detect which one it is talking to. Bowire uses the extension when the server advertises it, and the 2025-11-25 shape when that revision was negotiated and the tool declares task support.

## Trying it

The MCP sample in the Bowire repository is a task-capable server built on the C# SDK:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<SampleTools>()
    .WithTasks(new InMemoryMcpTaskStore(), o => o.ExecutionModeSelector = ctx =>
        ctx.MatchedPrimitive is McpServerTool { ProtocolTool.Name: "generate_report" or "confirm_shipment" }
            ? McpTaskExecutionMode.Optional
            : McpTaskExecutionMode.Synchronous);
```

Run it with `dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp` and open `http://localhost:5190/bowire`, or point a standalone Bowire at any task-capable server with `bowire --url mcp@http://localhost:5190/mcp`. Call `generate_report` and watch the task card move from *working* to *completed*; call `confirm_shipment` to answer an input request; cancel a running report to see cooperative cancellation for yourself. From the CLI or REST, the same operations are a `Tasks` service with `get`, `follow`, `update` and `cancel`.

Task support ships in Bowire 2.10. The [MCP protocol page](/docs/protocols/mcp.html#tasks--long-running-tool-calls) has the details, including what is not answered yet (input requests of 2025-11-25 tasks, and sampling).

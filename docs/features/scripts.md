---
title: Pre-request and post-response scripts
summary: 'Change a request before it goes out and check the response after it comes back, with a small JavaScript API, in the workbench and in bowire test alike.'
---

# Pre-request and post-response scripts

A method can carry two scripts, written in its **Scripts** tab:

- the **pre-request script** runs before the request goes out and may change it: sign a header, add a query parameter, rewrite the body;
- the **post-response script** runs after the response arrives and reads it: check it, keep a value for the next request, write a log line.

Both are JavaScript with one object in scope, `ctx`. The same script runs in the workbench and under `bowire test`, with the same `ctx` and the same outcome.

## The `ctx` object

Available in every script:

| Member | What it does |
|---|---|
| `ctx.env.get(name)`, `ctx.env.set(name, value)`, `ctx.env.keys()` | Read the environment; `set` writes the value into it, so later requests see it. |
| `ctx.vars.captured` | An object scripts share across the requests of a session or a run. |
| `ctx.assert.ok(value, message)`, `equal(actual, expected, message)`, `notEqual`, `deepEqual` | Stop the script with a failure. |
| `ctx.log(...)` | A line in the script's log; objects are written as JSON. |
| `ctx.protocol`, `ctx.service`, `ctx.method` | What the request is. |

What the request looks like depends on its protocol:

| Protocol | Pre-request | Post-response |
|---|---|---|
| REST, GraphQL, SSE, SignalR, OData, JSON-RPC | `ctx.request.url`, `.method`, `.body`, `.headers.get/set/delete/has/keys`, `.query.get/set/delete/has/keys` | `ctx.request` (read-only), `ctx.response.body`, `.status`, `.durationMs`, `.headers`, `.json()` |
| gRPC | `ctx.metadata.get/set/delete/has/keys`, `ctx.deadline.setSeconds(n)`, `ctx.request.body` | `ctx.metadata` (read-only), `ctx.response.metadata`, `.json()` |
| MQTT | `ctx.publish.qos`, `.retain`, `.topic`, `ctx.request.body` | `ctx.publish` (read-only), `ctx.response` |
| WebSocket, Socket.IO | `ctx.request.body` | `ctx.response` |

```js
// Pre-request: sign the request with a key from the environment.
var sig = ctx.env.get('apiKey') + ':' + ctx.request.method;
ctx.request.headers.set('X-Signature', sig);

// Post-response: check the response and keep the id for the next request.
var order = ctx.response.json();
ctx.assert.equal(ctx.response.status, 'OK');
ctx.vars.captured.orderId = order.id;
```

## In `bowire test`

**Export collection** in the Tests tab writes the method's scripts into the test collection, next to its assertions:

```json
{
  "name": "Create order",
  "service": "Orders",
  "method": "Create",
  "messages": ["{\"item\": \"x\"}"],
  "preScript": "ctx.request.headers.set('X-Signature', ctx.env.get('apiKey'));",
  "postScript": "ctx.assert.equal(ctx.response.json().state, 'open');",
  "assert": [ { "path": "status", "op": "eq", "expected": "OK" } ]
}
```

`bowire test` runs them as the workbench does:

- The pre-request script changes the request that goes out. If it throws or an assertion in it fails, the call is not made and the test fails with the script's message.
- The post-response script reads the response. Its failure fails the test like a failed assertion, listed as `post-response script`.
- `ctx.env.set` carries a value to the tests after it, and `ctx.vars.captured` is shared by every test of the run.
- `ctx.log` lines are printed under the test.

The scripts run in a sandbox: no file system, no process, no network, and limits on time, memory and statements. A script that reaches one is stopped and fails its test; the run goes on. The limits come from configuration:

| Setting | Default |
|---|---|
| `Bowire:Scripting:TimeoutMs` | 5000 |
| `Bowire:Scripting:MemoryMb` | 64 |
| `Bowire:Scripting:MaxStatements` | 10000000 |

The script engine is the package `Kuestenlogik.Bowire.Scripting`, which the `bowire` tool ships. A host that embeds Bowire without it gains no JavaScript engine; there, a test with a script fails, saying the package is not installed, rather than running without its script.

## One runtime, two places

The server side runs the workbench's own script runtime (`wwwroot/js/scripts.js`) in an embedded JavaScript engine, so `ctx` cannot drift between the two. A conformance suite runs the same fixtures through both and holds them to the same outcome.

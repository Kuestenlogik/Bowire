---
summary: 'Flows are a sequential multi-step editor for composing API interactions into repeatable pipelines.'
---

# Flows

Flows are a sequential multi-step editor for composing API interactions into repeatable pipelines. Unlike recordings (which capture what you did) and collections (which group saved requests), a flow is a designed sequence of nodes that you build intentionally and execute as a single unit.

## Flows vs. recordings vs. collections

| Feature | Recording | Collection | Flow |
|---------|-----------|------------|------|
| Created by | Capturing live calls | Saving individual requests | Building a node graph |
| Execution | Replays exactly what happened | Runs items in order | Runs nodes with logic between steps |
| Has conditions | No | No | Yes |
| Has delays | No | No | Yes |
| Variable capture | No | Response chaining only | Explicit Variable nodes |
| Editing | Delete steps only | Reorder/remove items | Add/remove/reorder any node type |

## Node types

Every flow is a linear sequence of nodes. Each node has a type and a badge shown in the editor.

### Request (REQ)

Invokes a method on a server. Stores the protocol, service name, method name, server URL, and request body. Variable substitution runs on the body before execution, so `${baseUrl}`, `${response.id}`, and other placeholders work as expected.

### Delay (WAIT)

Pauses execution for a configurable number of milliseconds. Useful for rate-limited APIs or when a service needs time to process a previous request before the next one fires.

### Condition (IF)

Evaluates a condition against the last captured response. If the condition fails, the flow stops immediately. Condition nodes have three fields:

| Field | Description |
|-------|-------------|
| Path | A dot-separated path into the response JSON (e.g. `status`, `user.role`) |
| Operator | One of `eq`, `neq`, `gt`, `lt` |
| Value | The expected value to compare against |

Example: path `status` with operator `eq` and value `OK` checks that the previous response's `status` field equals `"OK"`.

### Variable (VAR)

Captures a value from the last response and stores it under a name for later use. The path follows the same dot-notation as condition nodes and request chaining (`response.items.0.id`).

## Creating a flow

1. Open the Flow editor from the sidebar or the **+** menu.
2. Click the **+** button in the editor to create a new flow. It is named "New Flow" by default -- click the title to rename.
3. Use the node buttons at the bottom of the flow to add steps:
   - **+ Request** -- adds a Request node pre-filled with the currently selected service, method, and body
   - **+ Delay** -- adds a 1000 ms delay (editable)
   - **+ Condition** -- adds a condition that checks `status eq OK`
   - **+ Variable** -- adds a variable capture node

Nodes are rendered vertically with connector lines between them. Click the trash icon on any node to remove it.

## Running a flow

Click **Run** to execute the flow from top to bottom. The editor highlights the currently executing node and appends a PASS or FAIL badge to each completed node.

Execution follows these rules:

- **Request nodes** invoke the method via `/api/invoke`. The response is captured for chaining and condition evaluation.
- **Delay nodes** wait the specified duration, then move on.
- **Condition nodes** evaluate against the last captured response. If the condition is **not met**, the flow stops with status `stopped` and the condition shows FAIL.
- **Variable nodes** walk the response JSON at the given path. If the path resolves, the variable is stored and shows PASS. If the path does not exist, the node shows FAIL (but execution continues).

Variable substitution runs on every Request node body using the current environment, global variables, and any `${response.X}` placeholders from the most recent captured response.

## Condition operators

| Operator | Meaning | Comparison |
|----------|---------|------------|
| `eq` | Equals | String comparison after `String()` conversion |
| `neq` | Not equals | Inverse of `eq` |
| `gt` | Greater than | Numeric comparison via `Number()` |
| `lt` | Less than | Numeric comparison via `Number()` |

If no operator is specified, the condition checks for truthiness -- any non-null, non-empty value at the given path passes.

## Variable capture example

```
Flow: "Create and verify user"

1. [REQ]  UserService/CreateUser    body: { "name": "Alice" }
2. [VAR]  userId = ${response.id}
3. [WAIT] 500ms
4. [REQ]  UserService/GetUser       body: { "id": ${response.id} }
5. [IF]   response.name eq Alice
```

Step 1 creates a user and captures the response. Step 2 stores the `id` field. Step 3 waits half a second. Step 4 fetches the user using the captured ID. Step 5 verifies the returned name matches.

## Persistence

Flows are stored in `localStorage` under the key `bowire_flows`. They survive browser reloads but are scoped to the current browser and Bowire instance URL.

## Tips

- Use flows for **integration test sequences** -- create a resource, verify it, update it, delete it, confirm deletion.
- Combine condition nodes with delays to **poll** an async endpoint until a status changes.
- A flow's Request nodes use the **current environment** at run time, so you can switch environments and rerun the same flow against a different server.
- The flow editor reuses the same modal layout as the recordings and collections managers -- the left panel lists flows, the right panel shows the selected flow's detail.

See also: [Recorder](recording.md), [Collections](collections.md), [Request Chaining](response-chaining.md), [Environments & Variables](environments.md)

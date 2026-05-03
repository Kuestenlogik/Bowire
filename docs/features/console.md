---
summary: 'The Console is a chronological slide-up panel that logs every request, response, stream message, and channel event in the order they happen.'
---

# Console / Log View

The Console is a chronological slide-up panel that logs every request, response, stream message, and channel event in the order they happen. It's distinct from [History](favorites-history.md) -- history is method-centric and persisted across reloads, the console is **session-centric and ephemeral**, optimized for watching sequences of activity in real time.

## Opening it

Click the **Console** button at the right end of the action bar (it has a clock icon and shows a count badge once activity has been logged). Click it again -- or use the close button in the panel header -- to hide it.

The panel slides up from the bottom and takes 280 pixels of vertical space. Content above is not pushed -- the panel sits on top.

## Anatomy of an entry

Each entry is a single line by default, expandable to show the full body:

```
14:32:18.412   REQ   todo.TodoService/GetTodo               { "id": 7 }
14:32:18.486   RES   todo.TodoService/GetTodo   OK   74 ms  { "id": 7, "title": "Buy milk", ... }
```

| Column | Meaning |
|--------|---------|
| **Time** | Local time with millisecond precision |
| **Type** | REQ / RES / STR / SND / CH / ERR (see below) |
| **Method** | `service/method` of the call |
| **Status** | gRPC status name (OK, NotFound, ...) when relevant |
| **Duration** | Round-trip time in milliseconds |
| **Preview** | First 80 characters of the body, single-line |

Click the line to **expand** it -- the full body appears as pretty-printed JSON below the summary. Click again to collapse.

## Entry types

| Type | Color | When |
|------|-------|------|
| **REQ** | blue | A request is about to be sent |
| **RES** (ok) | green | A successful response received (status OK / Completed / Connected) |
| **RES** (warn) | yellow | A response with a non-OK gRPC status (NotFound, InvalidArgument, ...) |
| **STR** | accent | A single message received over a server-streaming or channel SSE flow |
| **SND** | accent | A message sent over an open duplex / client-streaming channel |
| **CH** | accent | Channel lifecycle events (open, close) |
| **ERR** | red | Network errors, channel failures, send/connect errors |

The left edge of each row is colored by type so you can scan the panel quickly without reading every cell.

## Buffer behavior

The console keeps **at most 200 entries** in a ring buffer. Once full, the oldest entry is dropped to make room for the newest. The count badge in the header shows the current entry count.

The buffer:

- **Lives in memory only** -- never persisted to disk
- **Resets on page reload** -- there's no way to recover entries after a refresh
- **Auto-scrolls to the latest entry** when the panel is open and a new event arrives
- **Stops auto-scrolling implicitly** when the panel is closed (entries still accumulate, you'll see them on next open)

## Clear

The **Clear** button in the panel header empties the buffer. There is no confirmation -- it's local-only state and easily reproducible by sending another request.

## Use cases

| Goal | How the console helps |
|------|------------------------|
| Debugging a request sequence (A → B → C) | See all three calls with their bodies on a single timeline |
| Validating [request chaining](response-chaining.md) | Expand the previous response and confirm the JSON shape before writing `${response.X}` |
| Watching a stream's throughput | Each STR row shows when a message arrived |
| Catching intermittent errors | ERR entries are red and stay in the buffer until cleared |
| Understanding channel state | CH rows mark open / close, SND / STR rows show every message in either direction |

## Differences from History

| | History | Console |
|--|---------|---------|
| **Storage** | localStorage (persists across reloads) | In-memory only |
| **Granularity** | One entry per **completed call** | One entry per **event** (request, response, stream message, etc.) |
| **Order** | Most recent first | Chronological (oldest first, scrolls to latest) |
| **Scope** | Per method | Whole session, all methods, all protocols |
| **Replay** | Click to replay the call with the same body | Click to expand body (no replay) |
| **Limit** | 50 entries | 200 entries |

History is for **"call this same thing again"**. Console is for **"what just happened, in what order?"**.

## Tips

- **Keep it open while testing chains** -- you can see the previous response shape and craft `${response.X}` placeholders without leaving the request pane.
- **Filter visually with colors** -- ERR is red, OK is green, anything else stands out at a glance.
- **Don't rely on it for audit logs** -- it caps at 200 entries and disappears on reload. For real audit needs, capture at the server side.

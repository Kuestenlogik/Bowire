---
summary: 'Capture a sequence of API calls and replay them as a regression test or a live mock.'
---

# Recording

Capture a sequence of API calls and replay them as a regression test or a live mock. Click the red record button in the action bar, make a few calls, click stop &mdash; the captured sequence becomes a named, replayable recording you can re-run with one click, convert into test assertions, or export as a HAR file.

## Recording a scenario

1. Click the red **● Record** button in the bottom action bar. The
   button switches to **Stop**, the icon pulses, and a small step
   counter appears next to the label.
2. Make any number of API calls — gRPC, REST, GraphQL, MCP, SSE,
   SignalR, WebSocket. Every successful invocation is captured into
   the active recording, including the request body, metadata,
   server URL, status, duration and response payload.
3. Click **Stop** when you're done. The recording is automatically
   named `Recording <timestamp>` and saved to disk at
   `~/.bowire/recordings.json`.

## Managing recordings

Shift-click the record button (or right-click) to open the recordings
manager. The left panel lists every recording you've ever made; the
right panel shows the selected recording's details:

- **Editable name** — click the title to rename
- **Step list** — every captured call with protocol badge, service /
  method, status, duration, and a per-step delete button
- **Action toolbar** — Replay, Convert to Tests, Export HAR, Export
  JSON, Delete

## Replay

Click **Replay** to re-run every step of the recording in order. The
modal highlights the running step and flips completed steps to a
green PASS or red FAIL badge with the live duration.

Variable substitution happens at **replay time** using the **current
environment** — not the environment that was active when the
recording was made. This makes recordings deliberately portable
across Dev / Staging / Prod: switch your env selector, hit replay,
the same scenario runs against the new target.

Streaming and channel methods are flagged with their `methodType`
and skipped during replay (the SSE / channel endpoints aren't routed
through `/api/invoke` so the replay path doesn't dispatch them).

## Convert to Tests

Click **Convert to Tests** to turn every captured response into a
regression suite. For each step, two assertions are appended onto
the recorded `(service, method)` pair:

1. `path=status, op=eq, expected=<captured-status>`
2. `path=response, op=eq, expected=<captured-response-body>`

Existing manual assertions on those methods are **left in place** —
the convert is append-only, never overwrites. After conversion, every
matching method auto-runs the new assertions on its next invocation
and you see PASS / FAIL right in the response pane.

## Export HAR

Click **Export HAR** to download the recording as an HTTP Archive
1.2 document. Each step becomes one HAR entry with a synthetic HTTP
request + response, the captured `durationMs` lands in
`timings.wait`, and the original protocol name is preserved on the
request `comment` field.

The HAR format is recognised by Chrome DevTools, Firefox DevTools,
Postman, Insomnia, Charles Proxy, Fiddler — the standard interchange
format for HTTP traces. Drop the file into any of these tools to
inspect, replay, or share with teammates who don't have Bowire
installed.

## Export JSON

Click **Export JSON** to download the raw Bowire recording document
(`{id, name, description, createdAt, steps[]}`). The JSON is
human-readable, ideal for committing to a repo as a regression
fixture, or sharing with other Bowire users (who can drop it
into their `~/.bowire/recordings.json`).

## How it persists

Recordings live at `~/.bowire/recordings.json` (next to the existing
`environments.json`). The on-disk file is the source of truth; the
browser keeps a localStorage cache for instant updates without server
round-trips, and writes back to disk via a debounced PUT to
`/bowire/api/recordings` after every step.

The `RecordingStore` C# helper validates JSON on save (refuses to
overwrite the file with garbage) and falls back to the empty default
shape when the file is missing or corrupt — the UI keeps working in
both cases.

## Implementation references

- C# store: `Kuestenlogik.Bowire.RecordingStore`
- C# endpoints: `Kuestenlogik.Bowire.Endpoints.BowireRecordingEndpoints`
  (`GET / PUT / DELETE /bowire/api/recordings`)
- JS state machine + UI: `wwwroot/js/recording.js`
- Capture hook: every `addHistory` call site in
  `wwwroot/js/api.js` (`invokeUnary`, `invokeStreaming`) and
  `wwwroot/js/protocols.js` (channel handlers) also calls
  `captureRecordingStep` when a recording is active

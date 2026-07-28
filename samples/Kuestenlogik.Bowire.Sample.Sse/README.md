# Kuestenlogik.Bowire.Sample.Sse

A Server-Sent Events ticker (`/events`, one `tick` per second) plus a
multi-line variant (`/events/report`), demonstrating **both** ways Bowire
meets an SSE service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `sse-catalogue.json` seeds the Sources rail with this host's streams.
  Both endpoints are *discovered*, not typed in as URLs: `/events` carries
  the `[SseEndpoint]` attribute, `/events/report` is additionally registered
  with `AddBowireSseEndpoint` — the two mechanisms from
  [docs/protocols/sse.md](../../docs/protocols/sse.md). Manual registrations
  win the dedup, so the report's fluent name is the one the rail shows.
- **Separate** — it is a real SSE endpoint, so point an external workbench
  or the CLI at it.

## What the stream exercises

The whole `text/event-stream` grammar the SSE subscriber parses, so every
field has a runnable target:

| Field | Where |
|-------|-------|
| `id:` | every frame — a monotonic sequence, shared by both endpoints |
| `event:` | `tick` on `/events`, `report` on `/events/report` |
| `data:` | one line on `/events`, **several lines** on `/events/report`, which the subscriber joins back with `\n` |
| `retry:` | once per connection, right after connect |
| `:` comment | a keep-alive every five seconds |

A background pump fills a bounded 256-tick buffer whether or not anyone is
subscribed, so there is a real gap to resume across. A client that sends the
`Last-Event-ID` request header is replayed from that point; a fresh client
without the header joins live at the end of the buffer instead of getting the
whole window dumped on it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Sse
```

- Embedded workbench: <http://localhost:5186/bowire> — both streams are
  already in the Sources rail. Put `Last-Event-ID` into the request metadata
  to resume from an id you saw earlier.
- As a separate target:

  ```pwsh
  bowire --url sse@http://localhost:5186/events
  ```

## From the shell

```pwsh
# Subscribe, note the ids, then resume after one of them.
curl -N http://localhost:5186/events
curl -N -H "Last-Event-ID: 42" http://localhost:5186/events

# The multi-line data: payload.
curl -N http://localhost:5186/events/report
```

The resumed stream opens with `: resuming after id 42` and its first frame
is `id: 43` — the buffered backlog first, then live.

# Kuestenlogik.Bowire.Sample.Sse

A Server-Sent Events ticker (`/events`, one `tick` per second)
demonstrating **both** ways Bowire meets an SSE service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `sse-catalogue.json` seeds the Sources rail with this host's `/events`
  stream.
- **Separate** — it is a real SSE endpoint, so point an external workbench
  or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Sse
```

- Embedded workbench: <http://localhost:5186/bowire> — the ticker stream
  is already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url sse@http://localhost:5186/events
  ```

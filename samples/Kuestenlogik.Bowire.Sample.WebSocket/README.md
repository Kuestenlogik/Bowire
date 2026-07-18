# Kuestenlogik.Bowire.Sample.WebSocket

A WebSocket echo server (`/ws`, every text frame echoed back prefixed
with `echo: `) demonstrating **both** ways Bowire meets a WebSocket
service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `websocket-catalogue.json` seeds the Sources rail with this host's `/ws`
  endpoint.
- **Separate** — it is a real WebSocket server, so point an external
  workbench or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.WebSocket
```

- Embedded workbench: <http://localhost:5185/bowire> — the echo endpoint
  is already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url websocket@ws://localhost:5185/ws
  ```

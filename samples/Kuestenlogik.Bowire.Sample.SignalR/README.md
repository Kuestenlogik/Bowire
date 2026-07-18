# Kuestenlogik.Bowire.Sample.SignalR

A SignalR chat host (one `ChatHub`: `SendMessage` broadcasts, `Echo`
round-trips) demonstrating **both** ways Bowire meets a SignalR service,
from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `signalr-catalogue.json` seeds the Sources rail with this host's
  `/chathub` hub.
- **Separate** — it is a real SignalR host, so point an external workbench
  or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.SignalR
```

- Embedded workbench: <http://localhost:5184/bowire> — the chat hub is
  already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url signalr@http://localhost:5184/chathub
  ```

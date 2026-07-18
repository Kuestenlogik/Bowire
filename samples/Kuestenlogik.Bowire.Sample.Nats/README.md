# Kuestenlogik.Bowire.Sample.Nats

A NATS sample that points at an **external** broker (NATS has no
.NET-embeddable server), while still telling both stories from one
project:

- **Embedded** — the workbench is mounted at `/bowire`, the bundled
  `nats-catalogue.json` seeds the Sources rail with the broker, and a
  resilient background publisher emits one message per second on
  `bowire.sample` so the subject has live traffic.
- **Separate** — point an external workbench or the CLI at the same
  broker.

The publisher is resilient: if the broker isn't up yet, the host +
workbench still start and it keeps retrying.

## Run

```pwsh
docker compose up            # start the NATS broker (JetStream on :4222)
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Nats
```

- Embedded workbench: <http://localhost:5193/bowire> — the broker is
  already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url nats://localhost:4222
  ```

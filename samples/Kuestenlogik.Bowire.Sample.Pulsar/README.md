# Kuestenlogik.Bowire.Sample.Pulsar

A Pulsar sample that points at an **external** broker (Pulsar has no
.NET-embeddable broker), while still telling both stories from one
project:

- **Embedded** — the workbench is mounted at `/bowire`, the bundled
  `pulsar-catalogue.json` seeds the Sources rail with the broker, and a
  resilient background producer publishes one message per second to
  `persistent://public/default/bowire-sample` so the topic has traffic.
- **Separate** — point an external workbench or the CLI at the same
  broker.

The producer is resilient: if the broker isn't up yet, the host +
workbench still start and it keeps retrying.

## Run

```pwsh
docker compose up            # start Pulsar standalone (:6650 + admin :8080)
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Pulsar
```

- Embedded workbench: <http://localhost:5194/bowire> — the broker is
  already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url pulsar://localhost:6650
  ```

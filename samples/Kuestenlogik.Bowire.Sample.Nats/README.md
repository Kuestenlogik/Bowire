# Kuestenlogik.Bowire.Sample.Nats

A NATS sample that points at an **external** broker (NATS has no
.NET-embeddable server), while still telling both stories from one
project:

- **Embedded** — the workbench is mounted at `/bowire`, the bundled
  `nats-catalogue.json` seeds the Sources rail with the broker, and a
  resilient background workload gives all three of the plugin's
  discovery sources something to report.
- **Separate** — point an external workbench or the CLI at the same
  broker.

The workload is resilient: if the broker isn't up yet, the host +
workbench still start and every piece keeps retrying — and it
re-establishes itself if the broker goes away mid-run.

## What the sample puts on the broker

| Discovery source | What the sample provides | Shows up as |
| --- | --- | --- |
| Subject sampling | Plain-text heartbeat on `bowire.sample`, a second one on `bowire.echo`, JSON readings on `telemetry.cpu` + `telemetry.memory` | services `bowire` and `telemetry` — two prefixes, so the sidebar's prefix grouping is visible |
| Subject sampling (req/reply) | A responder subscribed to `bowire.>` that answers anything with a reply subject | the discovered **Request** method returns an echo instead of timing out |
| JetStream | A `TELEMETRY` stream created at startup over the two `telemetry.*` subjects, fed by the heartbeat and capped at 1 000 messages | `JetStream:TELEMETRY` with `info`, `consume` and one JetStream-acked `publish` per subject — with real stored messages behind them |
| Services API | An `echo` service advertised over `$SRV.PING` with one `svc.echo.say` endpoint | `Service:echo` with a working req/reply method |

Two details worth knowing if you copy this sample:

- The `telemetry.*` readings are published with **core** publishes, not
  JetStream ones. The stream captures them either way — that is what a
  subject filter does — while a JetStream publish would round-trip its
  PubAck through an `_INBOX.*` reply subject that the wildcard subject
  scan would then list as a service of its own.
- The stream is configured with explicit subjects rather than a
  `telemetry.>` wildcard, because the plugin surfaces one publish method
  per filtered subject and a wildcard filter would surface a method
  nobody can publish to.

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

Any `nats-server -js` on `:4222` works — Docker is only the convenient
way to get one.

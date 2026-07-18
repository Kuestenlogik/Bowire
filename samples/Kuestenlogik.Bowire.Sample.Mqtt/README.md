# Kuestenlogik.Bowire.Sample.Mqtt

A **self-contained** MQTT sample — no docker needed. MQTT has a pure-.NET
embeddable broker (`MQTTnet.Server`), so this one project runs the broker,
a publisher, **and** the embedded workbench, demonstrating both ways
Bowire meets an MQTT broker:

- **Embedded** — an in-process broker on `:1883`, a publisher emitting one
  retained reading per second on `bowire/sample/sensor`, and the workbench
  at `/bowire` with the broker already in the Sources rail.
- **Separate** — the broker is a real listener, so point an external
  workbench or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mqtt
```

- Embedded workbench: <http://localhost:5192/bowire> — the sensor broker
  is already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url mqtt://localhost:1883
  ```

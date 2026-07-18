# Kuestenlogik.Bowire.Sample.Rest

An in-memory pet-store REST server (`/pets`, `/pets/{id}`, POST, DELETE)
with a .NET 10 native OpenAPI document, demonstrating **both** ways Bowire
meets a REST service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `rest-catalogue.json` seeds the Sources rail with this host. The REST
  plugin discovers the surface via `/openapi/v1.json`.
- **Separate** — it is a real REST server, so point an external workbench
  or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Rest
```

- Embedded workbench: <http://localhost:5181/bowire> — the pet-store
  operations are already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url rest@http://localhost:5181
  ```

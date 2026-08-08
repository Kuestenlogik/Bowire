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

## Checked-in project manifest

This sample ships a `.bowire/project.json` (the #172 convention) that declares
its source and security posture in version control, so CI and the PR bot resolve
the setup without per-job flags. Validate it with:

```pwsh
bowire project validate --file samples/Kuestenlogik.Bowire.Sample.Rest/.bowire/project.json
```

The samples-smoke workflow runs exactly this check for every sample manifest.

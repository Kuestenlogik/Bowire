# Kuestenlogik.Bowire.Sample.OData

An OData v4 Northwind-style server (`Categories` + `Products` at `/odata`
with `$metadata`) demonstrating **both** ways Bowire meets an OData
service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `odata-catalogue.json` seeds the Sources rail with this host's `/odata`
  endpoint. Bowire reads the CSDL/EDMX and surfaces the entity sets.
- **Separate** — it is a real OData server, so point an external workbench
  or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.OData
```

- Embedded workbench: <http://localhost:5188/bowire> — `Categories` and
  `Products` are already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url odata@http://localhost:5188/odata
  ```

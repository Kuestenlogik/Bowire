# Kuestenlogik.Bowire.Sample.OData

An OData v4 Northwind-style server (`Categories` + `Products` at `/odata`
with `$metadata`) demonstrating **both** ways Bowire meets an OData
service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `odata-catalogue.json` seeds the Sources rail with this host's `/odata`
  endpoint. Bowire reads the CSDL/EDMX and surfaces the entity sets.
- **Separate** — it is a real OData server, so point an external workbench
  or the CLI at it.

## What it serves

Bowire's OData plugin discovers five methods per entity set, and both
controllers implement all five — so every method in the Sources rail
answers for real instead of 404-ing on Execute:

| Method | Route | Result |
| --- | --- | --- |
| `GET` | `/odata/Products` | `$filter` `$select` `$expand` `$orderby` `$top` `$skip` `$count` |
| `GET_BY_KEY` | `/odata/Products(1)` | the entity, or 404 |
| `POST` | `/odata/Products` | 201 + `Location` |
| `PATCH` | `/odata/Products(1)` | 204, or 404 |
| `DELETE` | `/odata/Products(1)` | 204, or 404 |

Writes go into the singleton in-memory store, so a POST is still there on
the next GET.

`Category` and `Product` are joined by a real navigation property in both
directions, so `$expand` has something to expand:

```pwsh
curl "http://localhost:5188/odata/Products?`$expand=Category"
curl "http://localhost:5188/odata/Categories?`$expand=Products"
```

There is also one bound function, so the EDM advertises an operation and
not only CRUD — `DiscountedPrice(percent=…)` on `Product`:

```pwsh
curl "http://localhost:5188/odata/Products(1)/Default.DiscountedPrice(percent=15)"
curl "http://localhost:5188/odata/Products(1)/DiscountedPrice(percent=15)"   # unqualified also works
```

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

## Files

| File | What lives there |
| --- | --- |
| `Program.cs` | host, EDM model, the bound function, `MapBowire("/bowire")` |
| `Models.cs` | `Category` / `Product` and the seeded `NorthwindStore` |
| `Controllers.cs` | the two `ODataController`s — CRUD + the function |

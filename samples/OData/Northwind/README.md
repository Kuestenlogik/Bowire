# OData — Northwind sample

OData v4 endpoint at `/odata` with `Categories` and `Products` entity
sets. `$metadata`, `$select`, `$filter`, `$orderby`, `$expand` are all
enabled.

## Run

```pwsh
dotnet run --project samples/OData/Northwind
```

Listens on `http://localhost:5188`.

## Connect from Bowire

Server URL: `http://localhost:5188/odata`. The OData plugin fetches
`$metadata` (CSDL/EDMX) and surfaces both entity sets with their
queryable operations.

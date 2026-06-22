# REST — PetStore sample

In-memory pet store on minimal APIs with .NET 10's built-in
OpenAPI generator. Bowire's REST plugin discovers the surface
from `/openapi/v1.json`.

## Run

```pwsh
dotnet run --project examples/Rest/PetStore
```

Listens on `http://localhost:5181`.

## Connect from Bowire

Server URL: `http://localhost:5181`. Four operations show up under the
`Pets` controller — list, get-by-id, create, delete.

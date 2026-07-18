# Kuestenlogik.Bowire.Sample.GraphQL

A HotChocolate **Books** GraphQL server (Query + Mutation + Subscription)
that demonstrates **both** ways Bowire meets a GraphQL service, from one
project:

- **Embedded** — the workbench is mounted at `/bowire` in this process,
  and the bundled `graphql-catalogue.json` seeds the Sources rail with
  this host's `/graphql` endpoint (discovered via introspection).
- **Separate** — it is a real GraphQL server, so point an external
  workbench or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.GraphQL
```

- Embedded workbench: <http://localhost:5183/bowire> — `Books` is already
  in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url graphql@http://localhost:5183/graphql
  ```

Try `subscription { bookAdded { id title author } }`, then an `AddBook`
mutation, to see the subscription push over WebSockets.

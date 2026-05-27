# GraphQL — Books sample

HotChocolate-based GraphQL endpoint at `/graphql`. Three books in an
in-memory list, one mutation to add new ones.

## Run

```pwsh
dotnet run --project samples/GraphQL/Books
```

Listens on `http://localhost:5183`.

## Connect from Bowire

Server URL: `http://localhost:5183/graphql`. The plugin runs the
standard introspection query and surfaces `Query.books`,
`Query.bookById` and `Mutation.addBook`.

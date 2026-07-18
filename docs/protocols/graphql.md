---
title: GraphQL
summary: 'The GraphQL plugin connects to any GraphQL endpoint that supports the standard introspection query, surfaces every root operation as a Bowire service, and lets the user invoke que'
---

# GraphQL Protocol

The GraphQL plugin connects to any GraphQL endpoint that supports the standard introspection query, surfaces every root operation as a Bowire service, and lets the user invoke queries and mutations through the same form-based UI used by the gRPC and REST plugins.

**Package:** `Kuestenlogik.Bowire.Protocol.GraphQL`

## Setup

Standalone:

```bash
bowire --url http://localhost:8080/graphql
```

Point Bowire at the **GraphQL endpoint URL** itself (the one that accepts `POST { query, variables }`). The plugin sends the canonical introspection query and rebuilds the schema as Bowire services.

## Discovery

After a successful introspection, three services may appear in the sidebar:

| Service | Source | Notes |
|---------|--------|-------|
| **Query** | `__schema.queryType.fields` | Read-only operations. Method == GraphQL field. |
| **Mutation** | `__schema.mutationType.fields` | Write operations. |
| **Subscription** | `__schema.subscriptionType.fields` | Listed as server-streaming methods. Invocation is not yet implemented — see Limitations below. |

For each operation, arguments become Bowire form fields:

- `String` / `ID` → text input
- `Int` → number input (`int32`)
- `Float` → number input (`double`)
- `Boolean` → checkbox
- `[T]` → repeated field
- `T!` (NON_NULL) → required (asterisk in the form)
- Enums render as dropdowns populated from `__type.enumValues`
- Input objects (`INPUT_OBJECT`) recurse into nested form sections (depth-capped at 4)

Field descriptions, argument descriptions, default values, and `isDeprecated` flags from the schema all propagate to the UI.

## Invocation

When the user clicks **Execute**, Bowire builds a parameterised operation:

```graphql
query getBook($id: ID!) {
  getBook(id: $id) {
    __typename
  }
}
```

The variables come from the form (or the JSON editor) and are sent as the standard `{ query, variables }` payload. The full GraphQL response envelope (including `data`, `errors`, and `extensions`) is returned in the response viewer.

### Visual selection-set picker

For every method whose return type the introspection query exposed, Bowire renders a checkbox tree above the query editor. The tree is built by walking the discovered output type recursively (depth capped at 3, cycles broken by a visited-set so types like `User { friends: [User] }` terminate cleanly). Each checkbox corresponds to one field; nested object fields only render their children when the parent is checked, so the tree stays compact.

Toggling a checkbox updates the auto-generated query in the editor below — the operation regenerates with the exact selection you picked. Manual edits to the query editor still win: as soon as you type in the editor, your override sticks until you click "Reset to default" or use the "Select all (top-level)" / "Clear" buttons in the picker pane header.

Defaults: every top-level scalar is pre-checked the first time you open a method, so a fresh discovery comes with a usable query out of the box. Fields with no checkbox checked at any level fall back to `__typename` so the resulting query is always valid.

### Subscriptions

Bowire supports both major GraphQL subscription transports — both wired through the protocol-boundary interfaces in `Kuestenlogik.Bowire` core (`IInlineWebSocketChannel`, `IInlineSseSubscriber`) — so the GraphQL plugin can ride on the WebSocket or SSE plugins without taking a compile-time dependency on either.

| Transport | Spec | Default? |
|-----------|------|----------|
| `graphql-transport-ws` | https://github.com/enisdenjo/graphql-ws/blob/master/PROTOCOL.md | Yes (when the WebSocket plugin is loaded) |
| `graphql-sse` (single connection) | https://github.com/enisdenjo/graphql-sse/blob/master/PROTOCOL.md | Fallback when the WebSocket plugin isn't available |

Bowire tries WebSocket first because it's the canonical modern transport. To force a specific transport, set the metadata header `X-Bowire-GraphQL-Subscription-Transport` to `ws` or `sse` on the request. If neither transport is reachable (e.g. WebSocket plugin not installed and the server doesn't speak graphql-sse), Bowire yields a single error envelope with a clear "install Kuestenlogik.Bowire.Protocol.WebSocket" message instead of an empty stream.

The handshake / subscribe / next / complete lifecycle is fully handled — `connection_init` → `connection_ack` → `subscribe` (with the same query + variables shape as queries / mutations) → repeated `next` payloads → `complete`. `error` payloads from the server surface in the response stream as a JSON envelope with the GraphQL `errors` array.

## Authentication

Per-environment auth helpers (Bearer / Basic / API Key / JWT / OAuth) work exactly like for REST or gRPC: the headers are added to the POST that carries the GraphQL operation. There is no GraphQL-specific auth — it just rides on HTTP.

## Limitations

- **Custom scalars fall back to `String`.** The introspection query doesn't tell us how to coerce them, so we use the most permissive type and let the server's implicit coercion handle it.
- **Selection-set picker is depth-capped at 3.** Deeper nested objects render as "leaf" message fields you can check or uncheck, but you can't expand into their inner fields from the picker. Workaround: edit the query manually in the editor pane below.
- **Unions and interfaces** in the picker pick the first member type's fields. Inline fragment selection (`... on User { ... }`) is not yet exposed in the visual picker; edit the query manually if you need it.

## Try it with a public endpoint

Three well-known introspection-enabled GraphQL endpoints work as drop-in `--url` targets for the standalone tool. Each accepts anonymous queries:

| Endpoint | What's in it |
|----------|--------------|
| `https://countries.trevorblades.com/graphql` | Countries / languages / continents; small schema, fast — good first introspection target |
| `https://rickandmortyapi.com/graphql` | Characters / Episodes / Locations; bigger result sets, lists with pagination |
| `https://swapi-graphql.netlify.app/.netlify/functions/index` | Star Wars films / people / planets via Relay-style Connection / Edge pagination |

```bash
bowire --url https://countries.trevorblades.com/graphql
```

Bowire sends the canonical introspection query, rebuilds the schema, and the **Query** + **Mutation** services show up in the sidebar.

> These are third-party services that may rate-limit, slow down, or disappear without notice. Treat them as convenience for getting started — for sustained testing, run the [GraphQL sample](#sample) locally.

## Sample

[`samples/Kuestenlogik.Bowire.Sample.GraphQL`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.GraphQL) is a HotChocolate **Books** server (Query + Mutation + Subscription) that doubles as both stories from a single project. It exposes:

- `Query.books: [Book!]!` and `Query.bookById(id: Int!): Book`
- `Mutation.addBook(title, author): Book`
- `Subscription.bookAdded: Book` — pushes every newly-added book over the WebSocket transport
- A `Book` object type with `id`, `title`, `author`

Run on port 5183:

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.GraphQL
```

- **Embedded** — the workbench is mounted at `/bowire` in the same process, with the `Books` endpoint already seeded into the Sources rail. Open <http://localhost:5183/bowire>.
- **Separate target** — it's a real GraphQL server, so point the standalone tool (or any external workbench) at it:

  ```bash
  bowire --url graphql@http://localhost:5183/graphql
  ```

Try `subscription { bookAdded { id title author } }`, then an `addBook` mutation, to watch the subscription push over WebSockets.

See also: [Quick Start](../setup/index.md), [Plugin System](../features/plugin-system.md)

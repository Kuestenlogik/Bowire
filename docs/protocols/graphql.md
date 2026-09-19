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

### Transport options

Five metadata headers change how the operation travels. All are opt-in: the defaults — one POST
per document, a socket per subscription — work against nearly every server, and each option below
is unreachable or wrong somewhere, which is why none of them is automatic. Bowire strips the header
from the request it sends on.

| Header | Values | What it does |
|---|---|---|
| `X-Bowire-GraphQL-Subscription-Transport` | `ws`, `sse` | Forces the subscription transport instead of trying WebSocket first. |
| `X-Bowire-GraphQL-Http-Method` | `get` | Sends the query in the URL instead of a POST body. |
| `X-Bowire-GraphQL-Persisted-Query` | `on` | Automatic Persisted Queries: sends a SHA-256 of the document instead of the document. |
| `X-Bowire-GraphQL-Batch` | `on` | Sends several documents in one request, as the JSON array a batching server expects. |
| `X-Bowire-GraphQL-Multiplex` | `on` | Shares one `graphql-transport-ws` socket across subscriptions to the same endpoint. |

The three `on` flags also accept `true` and `1`; header names are matched case-insensitively.

**GET** exists for the two cases a POST cannot reach: a CDN or cache in front of the API, which can
only cache a GET, and a server that accepts queries over GET alone. It applies to **queries only** —
a mutation or subscription is refused with a message saying so, because GET is defined for queries
precisely because intermediaries may retry, prefetch or cache one. It also cannot carry file
uploads: a multipart body has no URL form, and that combination is refused rather than silently
dropping the files. Note what a GET costs — the whole document lands in the URL, where proxies and
access logs keep it, and a long document meets a URL length limit nobody controls.

**APQ** saves the whole document on every request after the first, which matters behind a
per-request body limit or on a link where the document is the expensive part. It costs an extra
round trip the first time a server sees a document: Bowire sends the hash, the server answers
`PERSISTED_QUERY_NOT_FOUND`, and Bowire retries with the document itself. A server that answers
`PERSISTED_QUERY_NOT_SUPPORTED` is remembered for the rest of the session, per endpoint, so the
refusal is paid once rather than on every call.

**Batching** is what makes the invoke list plural. Without the flag, a call carrying several
documents is refused with a message naming the flag — earlier versions read the first and dropped
the rest without a word. It does not combine with GET (an array has no sensible URL form) or with
APQ (that would need a hash per entry and a retry per miss, a convention servers do not agree on).

**Multiplexing** uses what the protocol was built for: every message carries an operation id, so one
connection can serve many. The pool key includes the request headers, so two subscriptions with
different credentials never share a socket and get answered as whoever connected first. When a
shared socket dies, every rider gets the stream-error frame rather than an empty completion. It is
opt-in because one failure now reaches several callers, and the socket-per-subscription path has
years of use behind it while this has none.

### File uploads

A mutation with an `Upload` variable travels as `graphql-multipart-request-spec`: an `operations`
part with `null` standing where each file goes, a `map` part saying which part fills which hole, and
one part per file. The **Files** tab in the request builder drives it — one row per file, with the
variable path (`variables.file`, or `variables.files.0` for a list entry) and the picker. The path
is the operator's call: the query alone does not say which variable takes a file. A path that
matches none of the variables the document declares is pointed out in the tab rather than coming
back as a server error nobody connects to it.

Two limits worth knowing: picked files do **not** survive a reload (the same rule as the REST binary
mode — the name is kept, the file reference is not), and a file over 25 MB is refused with a message
instead of a tab that freezes. The bytes travel twice on this path, base64 to the workbench and real
bytes onward, which is fine for hand-picked sizes and not for hundreds of megabytes.

## Authentication

Per-environment auth helpers (Bearer / Basic / API Key / JWT / OAuth) work exactly like for REST or gRPC: the headers are added to the POST that carries the GraphQL operation. There is no GraphQL-specific auth — it just rides on HTTP.

## Limitations

- **Custom scalars, enums and `ID` are edited as text.** Bowire's field vocabulary — `string`, `int32`, `message` — is shared across protocols so one form renderer can serve them all, and they all normalise to `string`. The *generated operation* does declare the schema's own type (`$id: ID!`, not `$id: String!`), so a server that does no implicit coercion still accepts it; what is normalised is the input widget, not the declaration. A request built without discovery — a stub from a pasted body — has no schema to read and still falls back to the JSON value's shape.
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

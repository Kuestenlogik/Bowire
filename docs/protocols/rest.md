---
summary: 'The Kuestenlogik.Bowire.Protocol.Rest plugin lets Bowire browse and exercise REST APIs alongside gRPC, SignalR, SSE, and MCP.'
---

# REST / OpenAPI

The `Kuestenlogik.Bowire.Protocol.Rest` plugin lets Bowire browse and exercise REST APIs alongside gRPC, SignalR, SSE, and MCP. Together with [Authentication](../features/authentication.md), [Environments](../features/environments.md), [Request Chaining](../features/response-chaining.md), and [Performance Graphs](../features/performance.md), it makes Bowire a single tool for testing every kind of HTTP-based API in your stack.

## Two discovery modes

The REST plugin discovers endpoints in **two completely different ways** depending on how Bowire is hosted:

| Mode | How | Cost |
|------|-----|------|
| **Embedded** | Reads `IApiDescriptionGroupCollectionProvider` from the host's service provider | No HTTP, no parsing -- instant |
| **URL** | Fetches the **exact** OpenAPI 3 / Swagger 2.0 document the user provides | One HTTP GET + one parse |

When the plugin is loaded inside an ASP.NET Core process via `app.MapBowire()`, embedded discovery wins automatically. The plugin reads exactly the same metadata that `Microsoft.AspNetCore.OpenApi` uses to generate its document, so there's no risk of stale schemas. URL discovery is only used when no service provider is available -- typically the standalone `bowire` CLI tool pointed at a remote server.

## Embedded discovery

Drop the REST plugin into a Minimal API or controller-based host and you're done. No configuration:

```csharp
using Kuestenlogik.Bowire;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();   // any OpenAPI generator works

var app = builder.Build();

app.MapGet("/todos/{id:int}", (int id) => Todos.Get(id))
    .WithName("GetTodo")
    .WithTags("Todos")
    .WithSummary("Get a todo by id")
    .WithDescription("Returns 404 if not found.");

app.MapBowire();  // <-- the REST plugin discovers all endpoints automatically
app.Run();
```

What gets extracted from the host:

| Bowire field | Source |
|---------------|--------|
| Service name | First `.WithTags()` value, or `[Tags]` attribute, or `ApiDescription.GroupName`, or `"Default"` |
| Method name | `.WithName(...)`, or sanitized `verb_path` |
| HTTP method + path | `ApiDescription.HttpMethod` + `RelativePath` (route constraints stripped) |
| Path / query / header / body params | `ParameterDescription.Source` (`BindingSource.Path`/`Query`/`Header`/`Body`) |
| Body fields | Walked from the body parameter's CLR type properties (one Bowire field per property) |
| Required | `ParameterDescription.IsRequired` |
| Summary | `IEndpointSummaryMetadata` (set by `.WithSummary(...)`) |
| Description | `IEndpointDescriptionMetadata` (set by `.WithDescription(...)`) |

Route constraints inside path templates are stripped automatically: `/users/{id:int}` becomes `/users/{id}` and matches the parameter name `id`.

## URL discovery

For standalone usage, point Bowire at the **exact OpenAPI document URL** -- not the API root, not a guess. The plugin makes one HTTP GET, parses the document, and reads `servers[0].url` from the spec for the actual API base URL where requests will be sent.

```bash
bowire --url=https://api.example.com/openapi.json
```

If the spec has no `servers` block, the plugin falls back to the doc URL's origin (e.g. `https://api.example.com` from the example above). For specs that declare relative server URLs (`{ "url": "/v2" }`), Bowire resolves them against the doc URL.

**There is no path probing.** If you typed the wrong URL, Bowire won't try `/swagger.json` or `/openapi.yaml` to "find it for you" -- you'll get a clear "no services discovered" message and you can correct the URL. This avoids the brittle, surprising behavior where a slightly-wrong URL silently picks up a stale schema cached at a different path.

The plugin handles JSON and YAML transparently and accepts both OpenAPI 3.x and Swagger 2.0 documents, courtesy of `Microsoft.OpenApi.Readers`.

## What's in the form

Each REST method shows the same form-based or raw-JSON editor as gRPC methods. Fields are annotated with **source badges** so you can tell at a glance whether a value goes into the URL path, the query string, an HTTP header, or the request body:

| Source | Color | Example |
|--------|-------|---------|
| `path` | orange | `id` in `/users/{id}` |
| `query` | blue | `?tag=work` |
| `header` | purple | `X-Api-Key` |
| `body` | green | JSON request body fields |

Required parameters get a red asterisk and the operation summary appears under the method name in the request pane header. Deprecated operations are struck through and tagged with `DEPR` in the sidebar.

## Invocation

`RestInvoker` reconstructs the HTTP request from the form values:

1. **Path parameters** are URL-encoded and substituted into `{name}` placeholders in the path template
2. **Query parameters** are appended as `?key=value&...` (arrays expand to repeated params)
3. **Header parameters** become request headers
4. **Body fields** are serialized as a single JSON document and sent with `Content-Type: application/json`
5. **Auth helper headers** from the active environment are merged in (manual metadata wins on case-insensitive collisions)

Verbs that don't carry a body (`GET`, `HEAD`, `DELETE`, `OPTIONS`, `TRACE`) skip body serialization even if you filled in body fields. The HTTP status code is mapped onto Bowire's existing color-coded status names so 2xx is green, 4xx is yellow, and 5xx is red in the action bar.

## Auth, environments, chaining, and benchmarks

The REST plugin doesn't reimplement any of these features -- they Just Work because they live one layer above the protocol plugin. You get:

- **[Authentication](../features/authentication.md)** -- Bearer / Basic / API Key / JWT / OAuth 2.0 (client_credentials, authorization_code+PKCE), AWS Sig v4, **mTLS** (PEM client cert + optional CA bundle), and a **per-environment cookie jar** for `POST /login` → `GET /me` flows
- **[Environments](../features/environments.md)** -- per-stage variables substituted into URL, headers, body
- **[Request Chaining](../features/response-chaining.md)** -- `${response.path}` from a previous REST response feeds into the next
- **[Console / Log View](../features/console.md)** -- chronological stream of REST calls
- **[Performance Graphs](../features/performance.md)** -- benchmark any unary REST endpoint with histogram + timeline

### mTLS

When the active environment carries an mTLS auth helper, the JS layer ships the PEM material as a `__bowireMtls__` metadata marker. `RestInvoker` parses it via the shared `Kuestenlogik.Bowire.Auth.MtlsConfig` (same code path the gRPC, WebSocket, SignalR, and Kafka plugins use) and creates a per-call `HttpClient` whose `HttpClientHandler` carries the client certificate plus an optional CA bundle. The marker is stripped from headers before the wire call, so the secret material never reaches the server as a regular header.

### Cookie jar

Mark a request with the `__bowireCookieEnv__` marker (the active environment id) and the call goes through a per-env `CookieContainer` instead of a stateless `HttpClient`. Subsequent requests against the same origin replay the cookies the server set — useful for sessions that depend on a `Set-Cookie: session=…` from `POST /login`. The container is in-memory only (process restart = logged out), and the workbench has a "Clear cookies" button per environment.

mTLS and cookie-jar mode compose: when both markers are present on a single request, the `CookieContainer` is attached to the same `HttpClientHandler` that already carries the client cert.

## Multipart / form-data

`multipart/form-data` operations are first-class. OpenAPI discovery walks request body content types and tags the operation as multipart when one of `multipart/form-data` or `application/x-www-form-urlencoded` is declared. Each schema property becomes one form field; `type: string, format: binary` properties become file inputs.

In the request form, file fields accept either:

- a plain base64 string (filename stays empty on the wire), or
- an object `{ "filename": "photo.jpg", "data": "<base64>" }` to set the filename hint.

`RestInvoker` builds a `MultipartFormDataContent` body from those parts: text values become `StringContent`, binary values become `StreamContent` with the supplied filename. The `Content-Type` boundary header is set automatically.

## Multi-URL

Bowire accepts any number of `--url` flags so you can test microservices across multiple hosts in a single session:

```bash
bowire --url=https://users.example.com:50051 \
        --url=https://orders.example.com/openapi.json \
        --url=https://chat.example.com/notifications
```

Each URL is fetched independently. Every protocol plugin tries each URL — the matching one wins for that URL. The discovered services are merged into a single sidebar list, each tagged with its **origin URL** so invocations route back to the right host. You can also add and remove URLs in the sidebar at runtime when not running in locked mode (via the `+ Add URL` / `Refresh` controls under the URL list). Per-URL status dots show which discoveries succeeded.

The URL list is persisted in browser `localStorage` so user-added URLs survive reloads. In locked mode (when URLs come from `--url` flags), the list is read-only.

## Local schema upload

Don't have a running server but you do have an OpenAPI doc on disk? Use the **Schema Files** tab in the sidebar — the drop zone accepts `.proto`, `.json`, `.yaml`, and `.yml`. Bowire routes uploads by file extension: `.proto` goes through the proto parser, JSON/YAML through `Microsoft.OpenApi.Readers`. Uploaded services appear under "Schema Files" mode in the sidebar (separate from URL-discovered services), with the file name shown as their origin.

Upload also works programmatically:

```bash
curl -X POST "http://localhost:5080/bowire/api/openapi/upload?name=customers.yaml" \
     --data-binary @./customers.yaml
```

## Schema-aware nested body editing

In embedded mode, complex CLR body types are walked recursively into nested message fields. So a `CreateOrder` parameter with a `ShippingAddress` property of type `Address { Street, City, Zip }` shows up in the form as a nested `shippingAddress` block with three sub-fields, not as a flat raw JSON dump. Arrays of complex types become repeated message fields. Recursion depth is capped at 4 to keep cyclic types from blowing up the field tree.

OpenAPI URL discovery handles the same case via `OpenApiDiscovery.SchemaToMessage` which has always been recursive.

## Deprecation detection

For embedded mode, Bowire reads `[Obsolete]` attributes on Minimal API delegates and controller actions and surfaces them as `Deprecated = true`. Deprecated methods get a strikethrough name and a `DEPR` badge in the sidebar, plus a `DEPRECATED` label in the request pane header. URL discovery already used the OpenAPI document's `deprecated` field — both paths now produce the same result.

## What's still missing

Tracked on the [roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md):

- Code snippet export (curl, python, JavaScript)

## Sample

`Bowire.Samples/SimpleRest/` is a Minimal API host with `Microsoft.AspNetCore.OpenApi`, three tags (`Todos`, `Tags`, `Legacy`), all five common verbs, a deprecated endpoint, and `app.MapBowire()` for embedded discovery. Run it:

```bash
cd Bowire.Samples
dotnet run --project src/SimpleRest
```

Then open <http://localhost:5006/bowire> and you'll see Todos, Tags, and Legacy in the sidebar with no further configuration.

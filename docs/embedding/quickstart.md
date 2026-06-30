---
title: Quickstart — Embedded Bowire
summary: 'Build a webapi from scratch, add the Kuestenlogik.Bowire package, call AddBowire() + MapBowire(), open /bowire. Anchored on samples/Kuestenlogik.Bowire.Sample.Embedded.'
---

# Quickstart — Embedded Bowire

## What this gets you

A new ASP.NET Core webapi host that serves your own endpoints **and**
the Bowire workbench at `http://localhost:<port>/bowire`. The
workbench's REST plugin auto-discovers the host's OpenAPI document,
so the operator can browse, invoke, and replay the host's own routes
from the embedded UI.

## From-scratch walkthrough

```bash
dotnet new webapi -n MyApi
cd MyApi
dotnet add package Kuestenlogik.Bowire.Protocol.Rest
```

Installing `Kuestenlogik.Bowire.Protocol.Rest` transitively pulls in
`Kuestenlogik.Bowire` (the core package) — you don't list both. See
[Embedded mode setup](../setup/embedded.md#how-packages-are-organised)
for the package-organisation rationale.

Edit `Program.cs`:

```csharp
using Kuestenlogik.Bowire;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBowire();           // <- registers plugins via assembly scan
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();          // .NET 10 native OpenAPI doc

var app = builder.Build();

app.MapOpenApi();                       // serves /openapi/v1.json
app.MapGet("/api/health", () => new { status = "ok" });

app.MapBowire();                        // <- workbench at /bowire (default)

app.Run();
```

Run:

```bash
dotnet run
```

Open `http://localhost:5000/bowire`. The REST plugin probes
`/openapi/v1.json`, the discovered endpoints land in the sidebar, and
you can invoke `GET /api/health` from the workbench's Compose rail.

## Code-anchored walkthrough — Sample.Embedded

Everything above is the minimum. The canonical sample
([`samples/Kuestenlogik.Bowire.Sample.Embedded`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Embedded))
adds the interceptor + the map widget. Walk-through:

### `Kuestenlogik.Bowire.Sample.Embedded.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Kuestenlogik.Bowire.Sample.Embedded</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Kuestenlogik.Bowire\Kuestenlogik.Bowire.csproj" />
    <ProjectReference Include="..\..\src\Kuestenlogik.Bowire.Protocol.Rest\Kuestenlogik.Bowire.Protocol.Rest.csproj" />
    <ProjectReference Include="..\..\src\Kuestenlogik.Bowire.Map\Kuestenlogik.Bowire.Map.csproj" />
    <ProjectReference Include="..\..\src\Kuestenlogik.Bowire.Interceptor\Kuestenlogik.Bowire.Interceptor.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
  </ItemGroup>

</Project>
```

What each reference contributes:

- `Kuestenlogik.Bowire` — the core package (workbench HTML/JS, the
  `MapBowire()` extension, `AddBowire()`, `BowireOptions`).
- `Kuestenlogik.Bowire.Protocol.Rest` — the REST plugin. Probes the
  host for an OpenAPI document and turns it into the services tree
  visible in the Discover rail.
- `Kuestenlogik.Bowire.Map` — the MapLibre map widget. Carries the
  `MapLibreExtension` class tagged with `[BowireExtension]`; on
  assembly load the workbench's extension registry picks it up and
  mounts the map widget over any `coordinate.wgs84` shape it sees.
- `Kuestenlogik.Bowire.Interceptor` — the package that ships
  `app.UseBowireInterceptor()`, the in-process middleware that records
  every request through the host into the workbench's Interceptor
  rail. Lifted out of Core in v2.1; embedded hosts that want it
  reference the package explicitly.
- `Microsoft.AspNetCore.OpenApi` — .NET 10's first-party OpenAPI
  generator. The REST plugin needs **some** discoverable
  spec; this is the one the sample uses.

### `Program.cs` line by line

```csharp
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Interceptor;
```

Two namespaces: `Kuestenlogik.Bowire` for `AddBowire()` /
`MapBowire()` / `BowireOptions`, and `Kuestenlogik.Bowire.Interceptor`
for `UseBowireInterceptor()`.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBowire();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
```

`AddBowire()` does three things at AddServices-time
([`BowireServiceCollectionExtensions.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireServiceCollectionExtensions.cs)):

1. Force-loads every `Kuestenlogik.Bowire*.dll` in the entry assembly's
   output directory so plugins that haven't been touched by the CLR
   show up in the subsequent reflection pass.
2. Walks every loaded `Bowire`-named assembly, instantiates every
   `IBowireProtocolServices` and `IBowireServiceContribution`
   implementation, and lets each register its services into the
   container.
3. Registers the frame-semantics pieces (the `LayeredAnnotationStore`,
   the five built-in detectors, the `IFrameProber`), plus the
   `BowireRecordingSession` singleton and the named HTTP client used
   by the auth endpoints.

`AddEndpointsApiExplorer()` + `AddOpenApi()` are .NET stock — needed
so that the REST plugin has a spec to read.

```csharp
var app = builder.Build();

app.UseBowireInterceptor();
```

Mounts the interceptor middleware. Every request through this host
(any client, any rail except `/bowire/*`) is tee'd into the
`InterceptedFlowStore` and surfaced live in the workbench's
Interceptor rail. The default `IgnoredPathPrefixes` is `["/bowire"]`
so the rail doesn't observe itself; bodies are capped at 1 MiB.
See [Interceptor feature](../features/proxy.md) for the rail UI and
[`BowireInterceptorOptions`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire.Interceptor/BowireInterceptorOptions.cs)
for every knob.

```csharp
app.MapOpenApi();
```

Standard .NET 10 — serves the host's OpenAPI document at
`/openapi/v1.json`. Bowire's REST plugin probes that exact path
during discovery.

```csharp
app.MapGet("/api/users", () => ...).WithName("ListUsers").WithTags("Users");
app.MapGet("/api/users/{id:int}", (int id) => ...);
app.MapPost("/api/users", (UserCreate body) => ...);
app.MapGet("/api/products", () => ...);
app.MapGet("/api/health", () => ...);
app.MapGet("/api/locations", () => locations);
app.MapGet("/api/locations/{id}", (string id) => ...);
```

Plain ASP.NET minimal-API endpoints. They appear in the OpenAPI
document, so Bowire's REST plugin discovers them automatically — no
Bowire-specific attributes, no extra registration. The locations
endpoints return objects with a `{ lat, lon }` coordinate field, which
the bundled `Wgs84CoordinateDetector` picks up so the map widget
auto-mounts. See the sample's
[README](https://github.com/Kuestenlogik/Bowire/blob/main/samples/Kuestenlogik.Bowire.Sample.Embedded/README.md)
for the map-widget demo flow.

```csharp
app.MapBowire("/bowire");
```

Mounts the workbench at `/bowire`. Signature:

```csharp
public static IEndpointRouteBuilder MapBowire(
    this IEndpointRouteBuilder endpoints,
    string pattern = "/bowire",
    Action<BowireOptions>? configure = null)
```

The `pattern` parameter is always authoritative — setting
`options.RoutePrefix` inside the configure callback is overwritten by
the pattern argument. See [Options](options.md) for the full
`BowireOptions` surface.

```csharp
app.MapGet("/", () => Results.Redirect("/bowire"));

app.Run();
```

Root redirect so a curious operator hitting `/` lands at the workbench.

## Run + verify

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Embedded \
    --urls http://localhost:5181
```

Then open `http://localhost:5181/bowire`. The sidebar lists
`Users`, `Products`, `Ops`, `Locations`. Invoking
`GET /api/locations` paints a MapLibre map under the JSON because
the coordinate detector + the `Kuestenlogik.Bowire.Map` reference
combine to mount the `coordinate.wgs84` widget — no per-field
configuration, no OpenAPI extension. Any request through the host
(including those invoked from the workbench itself, since the
interceptor only ignores `/bowire/*`) lands in the Interceptor rail.

## Decision rules — when this fits

Use embedded mode when:

- **You own the ASP.NET host** and want the workbench available
  during local dev without spinning up a sidecar.
- **You want the interceptor**. `UseBowireInterceptor()` is in-process
  by definition — the standalone Tool can't intercept a different
  process's traffic without a separate MITM proxy (`bowire proxy`).
- **You want auth-share**. The workbench rides the host's existing
  `UseAuthentication()` / `UseAuthorization()` pipeline — call
  `.RequireAuthorization("YourPolicy")` on the `MapBowire` return.
- **You want richer discovery**. Embedded plugins have access to the
  host's `IServiceProvider` via
  `IBowireProtocol.Initialize(IServiceProvider?)`, so gRPC reflection
  / SignalR hub enumeration / endpoint-metadata probing all work
  without going over the network.

Prefer the [Standalone Tool](../setup/standalone.md) when the target
isn't yours, isn't .NET, or you don't want to add a NuGet to it.

## Cross-links

- [Options](options.md) — the `BowireOptions` surface.
- [Lifecycle](lifecycle.md) — DI registration + startup ordering.
- [Deployment modes](deployment-modes.md) — Embedded vs Standalone vs
  middleware-only.
- [Embedded mode setup](../setup/embedded.md) — per-protocol package
  requirements (gRPC reflection, SignalR hubs, GraphQL introspection).
- [Interceptor feature page](../features/proxy.md) — the rail surface
  fed by `app.UseBowireInterceptor()`.
- [Map widget feature page](../features/map-widget.md) — the
  `coordinate.wgs84` widget seen in this sample.
- [API reference](../api/index.md).

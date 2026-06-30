---
title: Lifecycle — DI, startup, plugin discovery, auth
summary: 'What AddBowire() and MapBowire() actually do, in order. Plugin auto-discovery via the IBowireProtocol / IBowireProtocolServices / IBowireServiceContribution / IBowireEndpointContribution sweep. Interaction with the host''s authentication and routing pipeline.'
---

# Lifecycle

## What this gets you

A concrete picture of what happens when an embedded host calls
`builder.Services.AddBowire()` and then `app.MapBowire()` — what gets
registered, in what order, and how the workbench discovers protocols,
rails, modules, and endpoint contributions in the host's `AppDomain`.

Every claim here is anchored on
[`src/Kuestenlogik.Bowire/BowireServiceCollectionExtensions.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireServiceCollectionExtensions.cs),
[`src/Kuestenlogik.Bowire/BowireEndpointRouteBuilderExtensions.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireEndpointRouteBuilderExtensions.cs),
and [`src/Kuestenlogik.Bowire/BowireApiEndpoints.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireApiEndpoints.cs).

## Phase 1 — `builder.Services.AddBowire()`

The public surface is:

```csharp
public static IServiceCollection AddBowire(this IServiceCollection services);
public static IServiceCollection AddBowire(
    this IServiceCollection services,
    Action<BowireOptions>? configure);
```

The second overload exists for one reason: `SchemaHintsPath` is the
only option that has to be settled at AddServices time, because the
`LayeredAnnotationStore` singleton needs it at construction. Every
other option lives on `MapBowire`'s configure callback.

What `AddBowire` does, in source order:

1. **Force-load every `Kuestenlogik.Bowire*.dll`** from the entry
   assembly's output directory.
   .NET only loads referenced assemblies on first type touch, and the
   C# compiler strips unused references from the metadata table —
   walking `GetReferencedAssemblies()` isn't enough. The force-load
   pass ensures every plugin DLL deployed alongside the host shows up
   in the AppDomain before the reflection scan runs.

2. **Materialise the bootstrap options.** A new `BowireOptions` is
   constructed and the optional `configure` callback runs against it.
   Only `SchemaHintsPath` is read from this instance — the rest is
   discarded; `MapBowire` builds its own `BowireOptions` later.

3. **Register the semantics store** (`LayeredAnnotationStore` +
   `IAnnotationStore`). Layers wired in:
   - user-session layer (in-memory),
   - user-file layer (`~/.bowire/schema-hints.json` or the
     `SchemaHintsPath` override; empty string disables the layer),
   - project-file layer (`bowire.schema-hints.json` in the CWD, when
     present),
   - auto-detector layer (in-memory; filled by the frame prober),
   - plugin-hint pull-through (lazily resolved against the registered
     `BowireProtocolRegistry`).

4. **Register the frame detectors** — `Wgs84CoordinateDetector`,
   `GeoJsonPointDetector`, `ImageBytesDetector`, `AudioBytesDetector`,
   `TimestampDetector` (skipped when
   `BowireOptions.DisableBuiltInDetectors = true`), plus the
   `IFrameProber` singleton. The prober reads from
   `LayeredAnnotationStore.AutoDetectorLayer`.

5. **Register the recording session** (`BowireRecordingSession`
   singleton). Process-singleton so the workbench HTTP surface, the
   MCP tool surface, and any in-process capture hook share one source
   of truth.

6. **Register the plugin-update check** — `PluginUpdateCheckService`
   singleton, the `BowirePluginUpdateCheckOptions` binding (config
   section `Bowire:PluginUpdateCheck`), and the
   `PluginUpdateCheckHostedService`. The hosted loop short-circuits
   when `Enabled` is false (opt-in).

7. **Register the named OAuth HTTP client** — `IHttpClientFactory`
   entry `"bowire-oauth"`. Used by the auth-proxy endpoints in
   `BowireAuthEndpoints`.

8. **Run the contribution sweep.** For every loaded assembly whose
   `FullName` contains `"Bowire"`:
   - Find every concrete type implementing `IBowireProtocolServices`,
     `Activator.CreateInstance`, call its `ConfigureServices(services)`.
   - Find every concrete type implementing `IBowireServiceContribution`,
     `Activator.CreateInstance`, call its `ConfigureServices(services)`.

   `Kuestenlogik.Bowire.Interceptor` registers `InterceptedFlowStore`,
   `InterceptorMockStore`, and the `BowireInterceptorOptions` binding
   through this seam — Core never references the interceptor's types
   at compile time, so removing the package strips the entire stack.

   Exceptions from a single sibling's contribution are silently
   swallowed (no logger available at AddServices time) — a misbehaving
   plugin can't take down host startup.

## Phase 2 — `app.UseBowireInterceptor()` (optional)

When the host references `Kuestenlogik.Bowire.Interceptor` and calls
`app.UseBowireInterceptor()`:

```csharp
public static IApplicationBuilder UseBowireInterceptor(
    this IApplicationBuilder app,
    Action<BowireInterceptorOptions>? configure = null);
```

What happens at startup:

1. **Idempotent core registration.** `EnsureCoreServicesRegistered`
   resolves `InterceptedFlowStore` from the already-built
   `ApplicationServices`. If it's not there (i.e. the host called
   `UseBowireInterceptor` without `AddBowire` or
   `AddBowireInterceptorCore`), the middleware throws
   `InvalidOperationException` with a clear hint pointing at the
   canonical entry points — fail-fast instead of a generic "Unable to
   resolve service" error from inside the middleware's `Invoke`.

2. **Apply the configure callback** to the resolved
   `IOptions<BowireInterceptorOptions>.Value`. Mutations take effect
   on the next request.

3. **Mount the middleware** (`UseMiddleware<BowireInterceptorMiddleware>`).

Defaults the middleware honours:

- `MaxBodyBytes` = 1 MiB per side.
- `MaxRetainedFlows` = 1000 (FIFO eviction).
- `IgnoredPathPrefixes` = `["/bowire"]` so the rail never observes
  itself.
- `Enabled` = `true` (master kill-switch; flip to `false` to
  short-circuit with no body buffering, no stream wrapping, no store
  write).
- `MocksEnabled` = `true` (when the rule set is empty the check is
  free).

## Phase 3 — `app.MapBowire(...)`

The public surface, both overloads:

```csharp
public static IEndpointRouteBuilder MapBowire(
    this IEndpointRouteBuilder endpoints,
    string pattern = "/bowire",
    Action<BowireOptions>? configure = null);

public static IEndpointRouteBuilder MapBowire(
    this IEndpointRouteBuilder endpoints,
    Action<BowireOptions> configure);
```

`MapBowire` builds a fresh `BowireOptions`, runs the configure
callback, and then **overwrites `options.RoutePrefix` with the
trimmed `pattern`** — the pattern argument always wins.

Inside `BowireApiEndpoints.Map(...)`:

1. **Resolve a logger** from the host's DI for one-shot startup
   messages. Falls back to `NullLogger` if `ILoggerFactory` isn't
   registered.

2. **Discover the protocol registry**
   (`BowireProtocolRegistry.Discover(options.DisabledPlugins,
   logger)`). For each registered protocol:
   - Call `protocol.Initialize(endpoints.ServiceProvider)` so the
     plugin sees the host's `IServiceProvider`.
   - If `options.Mode == BowireMode.Embedded` AND the protocol
     implements `IBowireProtocolServices`, call
     `setup.MapDiscoveryEndpoints(endpoints)` so the plugin can mount
     its own discovery surface (e.g. gRPC Reflection). Exceptions are
     logged and skipped — a single bad plugin can't abort host startup.

3. **Discover rail and module contributions**
   (`BowireRailRegistry.Discover` + `BowireModuleRegistry.Discover`).
   Rails / modules explicitly registered via
   `AddBowireRail<T>()` / `AddBowireModule<T>()` are merged in alongside
   the auto-discovered ones.

4. **Map the HTML index route** at the prefix root (e.g.
   `GET /bowire`). The HTML is served outside the auth-gated group on
   purpose: the bootstrap HTML has to load before the user can sign
   in.

5. **Open an anonymous `MapGroup("")`** — the "Bowire group" — and
   call every per-feature endpoint extension method against it
   (`MapBowireDiscoveryEndpoints`, `MapBowireInvokeEndpoints`,
   `MapBowireChannelEndpoints`, `MapBowireUploadEndpoints`,
   `MapBowireEnvironmentEndpoints`, `MapBowireRecordingEndpoints`,
   `MapBowireRecordingSessionEndpoints`, `MapBowireCollectionEndpoints`,
   `MapBowireParallelEndpoints`, `MapBowirePresetEndpoints`,
   `MapBowireAuthEndpoints`, `MapBowireWorkspaceEndpoints`,
   `MapBowirePluginEndpoints`, `MapBowireSemanticsEndpoints`,
   `MapBowireSecurityEndpoints`, `MapBowireHelpEndpoints`,
   `MapBowireCatalogueEndpoints`).

6. **Discover endpoint contributions.** Walks every loaded
   `Kuestenlogik.Bowire*` assembly for concrete
   `IBowireEndpointContribution` implementations, instantiates each
   via its parameterless constructor, and calls
   `MapEndpoints(bowireGroup, basePath)` so sibling packages can
   splice endpoints into the auth-gated group. The interceptor
   contributes `/api/intercepted/*`, `/api/traffic/*`, and
   `/api/tools/reverse-proxy/*` through this seam.

7. **Apply the auth gate** when an `IBowireAuthProvider` is registered.
   `bowireGroup.RequireAuthorization(BowireAuthPolicies.Default)` is
   called exactly once; without a registered provider Bowire stays
   open (same as the laptop default).

## Plugin auto-discovery — the sweep in detail

Bowire walks `AppDomain.CurrentDomain.GetAssemblies()` and filters
to assemblies whose `FullName` contains `"Bowire"`. Inside each:

| Pass | Looking for | What it does |
|---|---|---|
| AddServices (in `AddBowire`) | `IBowireProtocolServices` concrete types | `ConfigureServices(IServiceCollection)` |
| AddServices (in `AddBowire`) | `IBowireServiceContribution` concrete types | `ConfigureServices(IServiceCollection)` |
| Map (in `MapBowire`) | `IBowireProtocol` (via `BowireProtocolRegistry.Discover`) | `Initialize(IServiceProvider?)` |
| Map (in `MapBowire`) | `IBowireProtocolServices` concrete types | `MapDiscoveryEndpoints(IEndpointRouteBuilder)` (embedded mode only) |
| Map (in `MapBowire`) | `IBowireEndpointContribution` concrete types | `MapEndpoints(bowireGroup, basePath)` |
| Map (via `BowireRailRegistry.Discover`) | `IBowireRailContribution` concrete types | added to rail catalogue |
| Map (via `BowireModuleRegistry.Discover`) | `IBowireModuleContribution` concrete types | added to module catalogue |

All passes match concrete (`!IsAbstract && !IsInterface`) types via
reflection and `Activator.CreateInstance(...)` — every contribution
implementation must have a public parameterless constructor.

For out-of-tree plugins that aren't deployed alongside the host's
binary, use [`AddBowirePlugins(pluginDir)`](../api/index.md) **before**
`AddBowire()` to force-load the DLLs into the default
`AssemblyLoadContext` (or one `BowirePluginLoadContext` per
sub-directory) so they show up in the subsequent sweep. The overload
`AddBowirePlugins(IConfiguration)` reads `Bowire:PluginDir` and
no-ops when the key is unset.

## Interaction with the host's pipeline

### Authentication

Bowire **does not** call `AddAuthentication` / `UseAuthentication` for
you. The host's existing auth pipeline keeps full control. Three
shapes embedded hosts use:

1. **Open workbench (laptop default).** No `IBowireAuthProvider`
   registered. Bowire's API endpoints stay open. Same posture as the
   standalone Tool's default.

2. **Auth-share with the host.** Call
   `.RequireAuthorization("YourPolicy")` on the `MapBowire` return —
   the workbench inherits whatever scheme + policy the host already
   uses:

   ```csharp
   app.MapBowire().RequireAuthorization("AdminOnly");
   ```

3. **Bowire-managed auth provider.** Call
   `services.AddBowireAuth(builder.Configuration)` before
   `AddBowire()` to register an `IBowireAuthProvider` plugin (e.g.
   `Kuestenlogik.Bowire.Auth.Oidc`). At `MapBowire` time the API
   surface is auto-gated with `BowireAuthPolicies.Default`; the HTML
   index route stays outside the gate so the sign-in page can render.
   `Bowire:Auth:ProviderId` selects the provider; provider-specific
   keys live under `Bowire:Auth:<id>:*`.

### Routing

`MapBowire` is an endpoint-route extension — it requires
`UseRouting()` (which `WebApplication` runs implicitly). The
workbench's HTML route is mounted at `basePath` (`/bowire` for the
default), every API route at `basePath/api/...`. The `pattern`
parameter is the only thing that controls the prefix; setting
`options.RoutePrefix` inside the callback is overwritten.

### CORS

Bowire ships no CORS policy of its own. When the host's `UseCors(...)`
is configured it covers the workbench too. Standard ASP.NET
ordering applies — `UseCors` before `MapBowire`.

### Order of middleware vs `MapBowire`

The canonical order (from `samples/Kuestenlogik.Bowire.Sample.Embedded`):

```csharp
var app = builder.Build();

app.UseBowireInterceptor();   // before endpoints so it sees every request
app.MapOpenApi();             // before MapBowire so the REST plugin discovers the doc
app.MapGet(...);              // host endpoints
app.MapBowire("/bowire");     // workbench (after endpoints — order doesn't matter for routing,
                              // but reads as "host first, workbench last")
app.Run();
```

## Decision rules

- **Always call `AddBowire()` before `Build()`** — every contribution
  pass runs in `ConfigureServices`. `MapBowire` after `Build()`
  consumes the resolved `IServiceProvider`.
- **For schema hints, use the `AddBowire(configure)` overload**, not
  the `MapBowire` one — see the property's docstring.
- **For everything else, use the `MapBowire(configure)` callback**.
- **`UseBowireInterceptor()` early** in the pipeline (typically right
  after the auth middleware) so it sees every inbound request before
  the host's endpoints execute.
- **Plugin disable list** can be set via `BowireOptions.DisabledPlugins`
  in the `MapBowire` callback, via `Bowire:DisabledPlugins` in
  `appsettings.json` (when surfaced through code), or via the
  standalone Tool's `--disable-plugin` flag (which funnels into the
  same list).

## Cross-links

- [Quickstart](quickstart.md) — call site walkthrough.
- [Options](options.md) — the `BowireOptions` surface consumed in
  Phase 3.
- [Deployment modes](deployment-modes.md) — when to embed vs not.
- [Plugin architecture](../architecture/plugin-architecture.md) — the
  descriptor registry pattern that backs the sweep.
- [Embedded-host customization](../architecture/embedded-host-customization.md)
  — picking by bundle vs per-package opt-in.
- [Authentication feature](../features/authentication.md).

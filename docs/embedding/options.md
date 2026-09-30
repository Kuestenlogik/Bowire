---
title: BowireOptions — embedded configuration
summary: 'Every public property on BowireOptions, what it controls, and how it binds from appsettings.json / code-side configuration / environment variables. Read straight from src/Kuestenlogik.Bowire/BowireOptions.cs.'
---

# Configuration — `BowireOptions`

## What this gets you

A single class — [`BowireOptions`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireOptions.cs)
— carries every host-side setting for the embedded workbench. You hand
an instance to the `MapBowire(...)` extension via its configure
callback. Defaults are chosen so a zero-config
`app.MapBowire()` produces a working workbench at `/bowire`.

This page documents every public property the class exposes today. If
something isn't here, it doesn't exist — don't try to set it.

## Code-anchored walkthrough

The class lives at
[`src/Kuestenlogik.Bowire/BowireOptions.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireOptions.cs).
Its docstring sums up the configuration pattern:

> Passed to `MapBowire(...)` via the `configure` callback. Defaults
> are chosen so that a zero-config `app.MapBowire()` produces a
> working embedded UI at `/bowire`.

The example from the class docstring:

```csharp
app.MapBowire(options =>
{
    options.Title        = "Payments API workbench";
    options.Description  = "Internal staging environment";
    options.Theme        = BowireTheme.Dark;
    options.ServerUrls.Add("https://payments.staging:443");
    options.ServerUrls.Add("https://notifications.staging:443");
    options.ShowInternalServices = false;
});
```

## Property reference

Every property listed below appears on the public surface of
`BowireOptions` in the version this doc pins to. Where the property
also has an `appsettings.json` binding, the canonical key is shown.

### `Title` (`string`, default `"Bowire"`)

Title shown in the browser tab and the top-left of the workbench
header.

```csharp
options.Title = "Payments API workbench";
```

### `Description` (`string`, default `"Multi-protocol API workbench"`)

Short tagline rendered below the title.

```csharp
options.Description = "Internal staging";
```

### `Theme` (`BowireTheme`, default `BowireTheme.Dark`)

Initial UI theme. Users can flip themes from the header toggle at any
time; their choice persists in `localStorage`, so this option only
seeds the first visit. Allowed values: `BowireTheme.Dark`,
`BowireTheme.Light`.

```csharp
options.Theme = BowireTheme.Light;
```

### `RoutePrefix` (`string`, default `"bowire"`)

URL path prefix at which the workbench is mounted, without the leading
slash. **Setting this inside the configure callback has no effect** —
the `pattern` parameter on `MapBowire(...)` always wins:

```csharp
app.MapBowire("/api-browser", options =>
{
    options.Title = "API Browser";
    // options.RoutePrefix = "ignored" — overwritten by "/api-browser"
});
```

### `ServerUrl` (`string?`, default `null`)

Single discovery URL. Kept for backwards compatibility; when set, it
is merged into `ServerUrls`. New code should use `ServerUrls`. In
embedded mode you typically leave this `null` — Bowire discovers
services against the host it is embedded in.

### `ServerUrls` (`List<string>`, default empty)

One or more discovery URLs for standalone or multi-target setups.
Every installed protocol plugin tries every URL in parallel; the
matching plugin wins per URL.

```csharp
options.ServerUrls.Add("https://payments.staging:443");
options.ServerUrls.Add("https://notifications.staging:443");
```

### `DisabledPlugins` (`List<string>`, default empty)

Plugin ids to exclude from the assembly-scan registry. Matched
case-insensitively against `IBowireProtocol.Id`.

```csharp
builder.Services.AddBowire();   // standard registration
app.MapBowire(options =>
{
    options.DisabledPlugins.Add("grpc");
});
```

Equivalent `appsettings.json`:

```jsonc
{
  "Bowire": {
    "DisabledPlugins": [ "grpc" ]
  }
}
```

(The `Bowire:DisabledPlugins` key is the canonical binding the docstring
on `DisabledPlugins` calls out; the standalone Tool's `--disable-plugin`
flag funnels into the same list.)

### `LockServerUrl` (`bool`, default `false`)

When `true`, the workbench is locked to its configured server URLs —
**enforced on the server, not just in the UI**. The server-URL input is
read-only, and every endpoint that dials a caller-named target refuses
any other URL with `403 Forbidden` and a problem-details body
(`type: urn:bowire:target-not-allowed`) before a single byte goes out:

| Endpoint | Target it checks |
|---|---|
| `POST /api/invoke`, `GET /api/invoke/stream` | `?serverUrl=` (or the default target) |
| `POST /api/channel/open` | `?serverUrl=` |
| `GET /api/services` | `?serverUrl=` |
| `POST /api/security/fuzz` | `target` and `target` + `httpPath` |
| `POST /api/parallel/start-local`, `POST /api/parallel/start` | every `targets[].url` |
| `POST /api/network/test` | `url` |
| `POST /api/auth/oauth-token`, `/oauth-code-exchange`, `/oauth-refresh` | `tokenUrl` — against the auth list, see [`AllowedAuthUrls`](#allowedauthurls-liststring-default-empty) |
| `POST /api/auth/custom-token` | `url` — auth list |
| `POST /api/auth-recordings/{id}/capture` | every request the auth flow sends, after `{{var}}` substitution — auth list |

Use it for CI, demos, shared or hardened deployments where users may
browse the pre-configured service but must not turn the Bowire host into
a relay to other hosts (cloud metadata endpoints, internal services).

The allowed set is `ServerUrl` + `ServerUrls` + [`AllowedServerUrls`](#allowedserverurls-liststring-default-empty),
plus — in embedded mode — the host's own origin, which is the target the
endpoints fall back to when no `serverUrl` is passed. That origin is taken
from the request's `Host` header, so an embedded host reachable under
foreign names should restrict `AllowedHosts` or set `ServerUrl`
explicitly.

URLs are compared after normalisation, so neither a plugin hint nor
spelling variants get around the check:

- the `hint@` prefix is stripped (`grpcweb@https://api` → `https://api`);
- scheme and host compare case-insensitively, a default port equals its
  explicit form (`https://api` = `https://api:443`), a trailing slash and
  query/fragment are ignored, dot segments are resolved;
- the path of an allowed entry is a **base path**: `https://api/v1` allows
  `https://api/v1/orders` but not `https://api/v10` or `https://api/`;
- `https://api@evil.test` is the host `evil.test` and is refused;
- a value that is not an absolute `scheme://` URL only matches an entry
  spelled the same way.

The standalone tool sets `LockServerUrl` whenever it is started with
`--url` (or `Bowire:ServerUrls`), so `bowire --url https://api` only ever
dials `https://api`.

### `AllowedServerUrls` (`List<string>`, default empty)

Extra targets the server-side endpoints may dial besides `ServerUrl` /
`ServerUrls`. A non-empty list turns the same enforcement on **without**
locking the UI: users can still type a URL, but anything outside the
configured set is refused with `403`. Entries follow the matching rules
above (base paths, hint prefix ignored).

```csharp
app.MapBowire(options =>
{
    options.ServerUrl = "https://payments.staging";
    options.AllowedServerUrls.Add("https://notifications.staging/api");
    options.AllowedServerUrls.Add("mqtt://broker.staging:1883");
});
```

The standalone tool binds it from `Bowire:AllowedServerUrls` in
`appsettings.json` (or `BOWIRE_Bowire__AllowedServerUrls__0=…`) and the
repeatable, comma-separable `--allowed-server-url` flag.

`Bowire:Parallel:TargetAllowlist` still applies to parallel runs on top of
this: a parallel target must pass both.

### `AllowedAuthUrls` (`List<string>`, default empty)

Identity-provider URLs the auth helpers may call on the caller's behalf:
the OAuth token proxies (`/api/auth/oauth-token`, `/oauth-code-exchange`,
`/oauth-refresh`), the custom-token proxy (`/api/auth/custom-token`) and
auth-flow capture (`/api/auth-recordings/{id}/capture`). Token endpoints
usually live on another host than the API, so they have their own list —
adding the IdP to `AllowedServerUrls` would also let invoke and discovery
reach it.

The auth helpers are checked whenever `LockServerUrl` is set or either
allowlist is non-empty. They may then call the server URLs (`ServerUrl`,
`ServerUrls`, `AllowedServerUrls`, the embedded host's own origin) plus
`AllowedAuthUrls`; anything else gets the same `403`. A non-empty
`AllowedAuthUrls` on its own restricts only the auth helpers.

The `403` body says how to allow the target: `allowWith` names the option
(`AllowedServerUrls` or `AllowedAuthUrls`) and `remedy` the matching CLI flag
with the target's origin filled in, e.g. *"If this is your identity
provider, allow it with --allowed-auth-url https://login.example.com"*. The
standalone tool also prints the hint in its startup banner while targets are
locked and no auth URL is listed.

```csharp
app.MapBowire(options =>
{
    options.ServerUrl = "https://payments.staging";
    options.LockServerUrl = true;
    // Covers …/realms/acme/protocol/openid-connect/token and friends.
    options.AllowedAuthUrls.Add("https://login.example.com/realms/acme");
});
```

While the check is active, the auth helpers also stop following redirects —
a `302` from an allowed token endpoint would otherwise carry the request to
a host that was never checked; it comes back as an upstream error instead.
Auth-flow capture checks each step on the wire, because step URLs are only
final after `{{var}}` substitution; a custom `IAuthFlowCapturer` that does
not implement the restricted `CaptureAsync(flowJson, allowTarget, ct)`
overload refuses to run on such a host rather than run unchecked.

The standalone tool binds it from `Bowire:AllowedAuthUrls` and the
repeatable, comma-separable `--allowed-auth-url` flag.

### `ShowInternalServices` (`bool`, default `false`)

When `true`, the sidebar lists well-known internal services such as
`grpc.reflection.v1alpha.ServerReflection` and the gRPC health
endpoint. Useful for debugging reflection itself; hidden by default
because it clutters the service tree for most users.

### `AutoCreateInitialWorkspace` (`bool?`, default `null`)

Host stance on seeding a workspace for first-run users. Resolved in
three layers, highest precedence first:

1. **This option**, when set to `true` or `false`. An explicit stance
   wins and the per-browser toggle is shown read-only.
2. **Settings → General → "Auto-create initial workspace"**, the
   per-browser toggle, when the operator has flipped it.
3. **The mode default**, when neither of the above applies:
   - `BowireMode.Embedded` → **on**. A first run seeds one workspace
     named after the host app and lands on the Discover rail, where
     the host's own API is already listed (embedded discovery runs
     against the request origin before first paint, so there is
     nothing for the operator to configure).
   - `BowireMode.Standalone` → **off**. The Home page shows the
     "Create your first workspace" CTA so the operator meets the
     concept before pointing the workbench anywhere.

The workspace name comes from the host: `Title` when you set one, else
the entry assembly's simple name, else the request origin.

Set `options.AutoCreateInitialWorkspace = false` to opt an embedded
host out — that restores the empty Home + Create-Workspace CTA. Setting
`true` in standalone (or passing `--auto-create-initial-workspace`)
seeds a workspace called "Personal" instead.

Workspace management is unaffected either way: the topbar workspace
chip and the Workspaces rail expose switch / create / rename / delete
in both modes.

> **Changed in 2.3** — the type went from `bool` to `bool?` so "unset"
> can mean "use the mode default". `options.AutoCreateInitialWorkspace
> = true` still compiles unchanged; only code that *reads* the property
> into a `bool` needs a `?? false`.

### `Mode` (`BowireMode`, default `BowireMode.Embedded`)

UI operating mode. Two values:

- `BowireMode.Embedded` — in-process: URL bar is hidden, services
  discovered via the host's `IServiceProvider`. Any URLs in
  `ServerUrls` are still used silently for transport-level discovery
  (e.g. MQTT broker introspection, OData `$metadata` fetches).
- `BowireMode.Standalone` — URL bar is visible and users can add /
  edit / remove discovery URLs at runtime. The standalone CLI tool
  flips this explicitly.

Defaults to `Embedded` because `app.MapBowire()` implies an in-process
host — leave it alone unless you are writing the standalone Tool.

### `ProtoSources` (`List<ProtoSource>`, default empty)

Proto file sources used when a gRPC server does not expose Server
Reflection. When both reflection and proto sources are available,
proto sources take precedence (they are considered the authoritative
schema).

```csharp
options.ProtoSources.Add(ProtoSource.FromFile("protos/weather.proto"));
options.ProtoSources.Add(ProtoSource.FromContent(@"
    syntax = ""proto3"";
    // ...
"));
```

`ProtoSource` ships two factory methods today: `FromFile(string path)`
and `FromContent(string protoContent)`.

### `SchemaHintsPath` (`string?`, default `null`)

Override for the user-local schema-hints file path. When `null` (the
default), Bowire resolves to `~/.bowire/schema-hints.json`. Setting
this to the empty string disables the user-local layer entirely; only
the project-local file (`bowire.schema-hints.json` in the working
directory, when present) and session edits contribute to
`User`-priority annotations.

`SchemaHintsPath` is the one property that has to be settled at
`AddBowire` time rather than `MapBowire` time, because the
`LayeredAnnotationStore` singleton needs it at construction. Pass it
through the `AddBowire` overload:

```csharp
builder.Services.AddBowire(options =>
{
    options.SchemaHintsPath = "/etc/bowire/schema-hints.json";
});
```

### `DisableBuiltInDetectors` (`bool`, default `false`)

When `true`, the five built-in `IBowireFieldDetector`s shipped by core
(WGS84 coordinate, GeoJSON Point, image bytes, audio bytes, timestamp)
are NOT registered. Useful for hardened deployments that want to ship
their own detector set without the built-ins racing them, or for tests
pinning a deterministic detector list. The `IFrameProber` singleton is
still registered — it just has nothing to run until the host adds its
own detectors to the container.

### `MapBasemap` (`string?`, default `null`)

Basemap the MapLibre map widget paints under its pins. Three shapes
the widget accepts:

- Named alias — `"osm"` (OpenStreetMap raster tiles), `"satellite"`
  (ESRI World Imagery), `"demotiles"` (MapLibre's demo vector style),
  or `"none"` for the offline blank-style fallback.
- Custom raster URL — anything with `{z}/{x}/{y}` placeholders is
  treated as a tile-server URL the widget wraps in its own raster
  style.
- Custom style URL — a URL ending in `.json` is treated as a MapLibre
  style.json that the map constructor consumes directly.

Unset (the default) means "let the widget pick its built-in default"
— currently the demotiles vector style. The opt-in aliases each
contact exactly one documented external host; no implicit external
egress happens until an operator sets this key.

Canonical `appsettings.json` binding the docstring calls out:

```jsonc
{
  "Bowire": {
    "MapBasemap": "osm"
  }
}
```

When the application code wants to set it directly:

```csharp
app.MapBowire(options =>
{
    options.MapBasemap = "satellite";
});
```

## Patterns for binding from `appsettings.json`

`BowireOptions` itself is not bound automatically. The properties that
have `appsettings.json` keys today — `Bowire:DisabledPlugins`,
`Bowire:MapBasemap`, `Bowire:PluginDir`, `Bowire:PluginUpdateCheck`,
`Bowire:Auth` — are consumed by specific code paths inside Bowire
(plugin registry, plugin update check, auth seam, map widget). To
funnel `appsettings.json` into the host-side `BowireOptions`, read the
config explicitly in the `MapBowire` callback:

```csharp
app.MapBowire(options =>
{
    var config = builder.Configuration;
    if (config["Bowire:Title"] is { } title) options.Title = title;
    foreach (var p in config.GetSection("Bowire:DisabledPlugins").Get<string[]>() ?? [])
    {
        options.DisabledPlugins.Add(p);
    }
});
```

## Configuration sources Bowire's own code reads

These are the keys Bowire reads directly — they take effect without
any code-side wiring in `MapBowire`:

| Key | What it does | Read by |
|---|---|---|
| `Bowire:DisabledPlugins` | Comma-/array-list of plugin ids to skip. | `BowireProtocolRegistry.Discover` (when populated into `BowireOptions.DisabledPlugins`) |
| `Bowire:PluginDir` | Directory of installed sibling plugins. | `AddBowirePlugins(IConfiguration)` |
| `Bowire:PluginUpdateCheck:Enabled` | Opt-in to the daily NuGet update check. Off by default — outbound calls are opt-in. | `BowirePluginUpdateCheckOptions` |
| `Bowire:Auth` | Auth provider id + per-provider config. | `AddBowireAuth(IConfiguration)` |
| `Bowire:MapBasemap` | Map widget basemap. | The map widget's `bowireMapBasemapSpec()` consumer |

## Decision rules

- **Default everything** unless you have a reason. The defaults work.
- **Use the `pattern` argument of `MapBowire`** — not
  `options.RoutePrefix` — to change the URL prefix. The configure
  callback's `RoutePrefix` value is overwritten.
- **Use `AddBowire(options => { options.SchemaHintsPath = ... })`**
  for `SchemaHintsPath`, not the `MapBowire` callback. `SchemaHintsPath`
  is the only AddServices-time option.
- **For per-plugin options** (gRPC proto sources are on
  `BowireOptions.ProtoSources`; everything else lives on the plugin's
  own options class) follow the per-plugin doc page in
  [Protocol Guides](../protocols/index.md).

## Cross-links

- [Quickstart](quickstart.md) — the call site.
- [Lifecycle](lifecycle.md) — when each option is consumed.
- [Embedded mode setup](../setup/embedded.md) — per-protocol package
  requirements.
- [API reference for `BowireOptions`](../api/index.md) — the generated
  API surface; ground truth if this page ever drifts.
- [`src/Kuestenlogik.Bowire/BowireOptions.cs`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire/BowireOptions.cs)
  — the source.

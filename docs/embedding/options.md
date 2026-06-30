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

When `true`, the server-URL input in the UI is read-only. Use for CI,
demos, or hardened deployments where the operator should browse the
pre-configured service but not point the workbench at other hosts.

### `ShowInternalServices` (`bool`, default `false`)

When `true`, the sidebar lists well-known internal services such as
`grpc.reflection.v1alpha.ServerReflection` and the gRPC health
endpoint. Useful for debugging reflection itself; hidden by default
because it clutters the service tree for most users.

### `AutoCreateInitialWorkspace` (`bool`, default `false`)

When `true`, the workbench seeds a default "Personal" workspace for
first-run users so they boot straight into a usable shell. The
v2.0 default is `false` — the Home page shows a "Create your first
workspace" CTA so the operator learns the workspace concept up front.

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

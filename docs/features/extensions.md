---
summary: 'UI extensions are opt-in visual widgets (maps, charts, audio players, image viewers) that mount on top of payloads when the data carries a known semantic kind.'
---

# UI Extensions

Where **protocol plugins** handle a wire protocol (gRPC, REST, Kafka, …), **UI extensions** handle a *payload semantic*. The two layers are deliberately separate: a protocol plugin carries opaque bytes; an extension recognises what those bytes mean and mounts a matching widget — a map for `coordinate.wgs84` fields, a chart for `time-series`, an audio player for `audio.opus`, and so on.

Both ship as NuGet packages and install through the same paths. The split exists so users who never touch a map don't pay for ~870 KB of WebGL JS on every workbench load.

## Available extensions

| Extension | Package | What it does |
|-----------|---------|--------------|
| MapLibre | `Kuestenlogik.Bowire.Extension.MapLibre` | Renders fields tagged with `coordinate.wgs84` (latitude/longitude pairs, GeoJSON geometries) as a MapLibre GL JS map. Read-only viewer + drag-to-edit pin for request fields. Bundles MapLibre + a default basemap. |

More extensions are planned (image viewer, audio player, time-series chart, JSON-tree differ) — each one targeting a different `semantic kind` from the [frame-semantics framework](../architecture/frame-semantics-framework.md).

## Installing an extension

### Standalone CLI

Same `bowire plugin install` subcommand the protocol plugins use:

```bash
bowire plugin install Kuestenlogik.Bowire.Extension.MapLibre
```

The package lands in `~/.bowire/plugins/`. On the next workbench load, Bowire registers the extension with the UI extension framework; the map widget mounts automatically the next time a payload with a matching semantic shows up.

To remove it, drop in `bowire plugin uninstall Kuestenlogik.Bowire.Extension.MapLibre`. To see which extensions are installed, `bowire plugin list` covers both protocol plugins and extensions (the `PackageType` column distinguishes `BowirePlugin` from `BowireExtension`).

### Embedded ASP.NET

Same `dotnet add package` path the protocol plugins use:

```bash
dotnet add package Kuestenlogik.Bowire.Extension.MapLibre
```

No host-side wiring needed beyond `AddBowire()` + `MapBowire()` — the extension assembly is picked up by the same plugin scanner and its registration runs on the first workbench request.

### Docker

The container image ships the same `bowire plugin install` subcommand. Use the host's `~/.bowire` volume-mount so the install persists across container restarts:

```bash
docker run --rm -v ~/.bowire:/home/app/.bowire \
    ghcr.io/kuestenlogik/bowire:latest \
    plugin install Kuestenlogik.Bowire.Extension.MapLibre
```

## How activation works

Extensions don't replace protocol plugins — they sit alongside them and react to **semantic annotations** on message fields. Annotations are produced by:

- **Auto-detection** at discovery time (the proto detector recognises `google.type.LatLng`, the OpenAPI detector recognises `format: lat-long`, etc.).
- **Manual override** — users can annotate any field through the **Semantics** picker in the sidebar.

Once a field carries the `coordinate.wgs84` annotation, every payload that visits that field surfaces a map tab alongside the JSON / form views. There's no per-method config to write — the extension subscribes to the semantic kind globally and Bowire routes matching payloads to it.

See [frame-semantics-framework](../architecture/frame-semantics-framework.md) for the detector → annotation → viewer pipeline and the extension-author contract (`window.BowireExtensions.register({...})`).

## Configuration

The MapLibre extension picks up two flags on the standalone CLI:

| Flag | Purpose |
|------|---------|
| `--map-basemap=<key>` | Switch the basemap: `osm` (default), `satellite`, `demotiles`, or a raw tile-URL template. |
| `--no-browser` | Skips the auto-open browser tab — same as for the workbench itself. |

In embedded mode, the corresponding `BowireOptions.MapBasemap` property controls the same value:

```csharp
app.MapBowire(options =>
{
    options.MapBasemap = "satellite";
});
```

## Writing your own extension

The widget contract lives entirely in JS:

```js
window.BowireExtensions.register({
    id: 'com.example.heatmap',
    kind: 'time-series.heatmap',
    bowireApi: '1.x',
    mount: (host, ctx) => { /* draw your widget */ },
    unmount: (host) => { /* tear it down */ }
});
```

The .NET side is a thin shell: a NuGet project with `PackageType=BowireExtension`, an embedded JS bundle, and a discovery descriptor pointing at `wwwroot/js/widgets/<bundle>.js`. Bowire serves the bundle from `/api/ui/extensions/<id>/<bundle>` and the workbench loads it on first use.

For the full contract — capability negotiation, the `ctx` surface (`frames$`, `selection$`, `theme`, `viewport`, `host`), the registration semantics, and what changes between API majors — see [frame-semantics-framework](../architecture/frame-semantics-framework.md), Extension framework section.

## Related

- [Plugin system](plugin-system.md) — the protocol-plugin layer extensions sit alongside.
- [frame-semantics-framework](../architecture/frame-semantics-framework.md) — the architecture for both detection and consumption.

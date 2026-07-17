---
title: Map widget
summary: 'Auto-mounts a MapLibre GL JS viewer whenever a response carries the `coordinate.wgs84` semantic kind. Bidirectional sync between JSON and map.'
---

# Map widget

The **Map widget** is a [UI extension](extensions.md) that auto-mounts a MapLibre GL JS viewer whenever a Bowire response carries the `coordinate.wgs84` semantic kind. It ships as the `Kuestenlogik.Bowire.Map` package and is referenced by `Bundle.Workbench`, so the standalone Tool ships it out of the box.

The widget is the canonical demo of Bowire's [extension framework](../architecture/frame-semantics-framework.md) — a UI extension that subscribes to a semantic kind globally, recognises matching payloads at render time, and mounts a domain-specific viewer alongside the JSON tree.

## When it appears

The widget mounts when a response payload contains one or more fields tagged with the `coordinate.wgs84` semantic kind. Tagging happens in two ways:

1. **Auto-detection** at discovery time. The `Wgs84CoordinateDetector` recognises three shape conventions:

   | Shape | Example |
   |---|---|
   | `{lat, lon}` | `{ "lat": 53.55, "lon": 9.99 }` |
   | `{latitude, longitude}` | `{ "latitude": 53.55, "longitude": 9.99 }` |
   | `{latitudeCoordinate, longitudeCoordinate}` | `{ "latitudeCoordinate": 53.55, "longitudeCoordinate": 9.99 }` (Rheinmetall TacticalAPI naming) |

   The detector matches case-insensitively via anchored regex and supports both top-level coordinates and coordinates nested inside arrays of feature objects.

2. **Manual annotation** through the Semantics picker in the sidebar. Right-click any field in a response, pick **Semantics → coordinate.wgs84**, and the widget mounts on the next render. The annotation persists to the workspace.

When the widget is loaded but no coordinates show up in the response, the map tab is hidden — no empty map, no chrome cost.

## Layout — tab or split

Server-streaming and unary response panes get a tab-strip with **JSON** + **Map** + a layout toggle:

- **Tab mode** (default) — JSON and Map share the response pane, switch via the tab strip.
- **Split mode** — the response pane splits in two: JSON on one side, Map on the other. Click the toggle again to flip back to tabs.

Per-method layout preference persists in localStorage so the operator's choice for each method is remembered. The split-layout decision is **extension-driven** — Core asks the framework `preferredSplitExtensionForMethod(svc, method)` rather than hard-coding the `coordinate.wgs84` kind. Layering stays clean: Map-specific code lives in `Kuestenlogik.Bowire.Map`, Core stays generic.

## Bidirectional JSON ↔ map sync

The widget keeps the JSON tree and the map in sync both directions:

| Action | Effect |
|---|---|
| **Hover** a `{lat, lon}` block in the JSON | Highlights the matching pin on the map (no scroll). |
| **Hover** a pin on the map | Highlights the matching JSON block (no scroll). |
| **Click** a pin on the map | Scrolls the JSON to the matching line + auto-expands every collapsed ancestor. |
| **Right-click** a coordinate in JSON | Surfaces `Center on map` + `Copy path` + `Copy ${response.X}`. |
| **Double-click** a pin OR a coordinate field | Copies the field's JSON path to the clipboard. |

A right-side gutter hint surfaces the semantic kind on hover (e.g. `wgs84 coordinate`) so the operator knows which fields the widget is binding to.

## Pin gestures (detail)

| Gesture | Effect |
|---|---|
| Hover | Highlight JSON, no scroll. |
| Single-click | Scroll + expand ancestors. |
| Double-click | Copy JSON path to clipboard. |
| Right-click | Menu — Copy path, Copy `${response.X}`, Center on map. |

The gestures were stabilised after operator review in v2.1 to feel like a real map application, not a JSON viewer that happens to draw pins.

## Basemap

The widget bundles MapLibre GL JS + a default basemap. The default is OpenStreetMap raster tiles via the `osm` key; the basemap can be swapped:

| Standalone CLI flag | Embedded option | Value |
|---|---|---|
| `--map-basemap=osm` | `options.MapBasemap = "osm"` | OpenStreetMap raster (default). |
| `--map-basemap=satellite` | `"satellite"` | Generic public satellite raster. |
| `--map-basemap=demotiles` | `"demotiles"` | MapLibre demo vector tiles — minimal styling, useful for offline. |
| `--map-basemap=<tile-url>` | `"<tile-url>"` | Raw tile-URL template (e.g. `https://tiles.example.com/{z}/{x}/{y}.png`). |

For air-gapped deployments, supply a tile-server URL on your own network. The widget never phones home — basemap fetches go directly from the browser to the configured tile URL.

## Installing the extension

### Standalone CLI

```bash
bowire plugin install Kuestenlogik.Bowire.Map
```

The package lands in `~/.bowire/plugins/`. On the next workbench load, Bowire registers the extension; the map widget mounts the next time a `coordinate.wgs84`-tagged payload shows up.

### Embedded ASP.NET

```bash
dotnet add package Kuestenlogik.Bowire.Map
```

No host-side wiring beyond `AddBowire()` + `MapBowire()` — the extension assembly is picked up by the plugin scanner and registers on the first workbench request.

### Docker

```bash
docker run --rm -v ~/.bowire:/home/app/.bowire \
    ghcr.io/kuestenlogik/bowire:latest \
    plugin install Kuestenlogik.Bowire.Map
```

`Bundle.Workbench` references the package transitively in v2.1, so the standalone Tool already ships the widget — the explicit install is only needed for embedded hosts that drop the bundle.

## Demo against TacticalApi.RadarSweep

The `TacticalApi.RadarSweep` sample server hosts Rheinmetall's `Situation` gRPC service with three MIL-2525C contacts orbiting a radar centre at 54.00°N / 11.50°E (off the German Baltic coast) so subscriptions feel live. It lives in the [`Bowire.Samples`](https://github.com/Kuestenlogik/Bowire.Samples) repo (`protocols/TacticalApi.RadarSweep`) and fetches the upstream `.proto` at build time, so it stays free of a dependency back on the plugin package. The TacticalAPI payload uses the `{latitudeCoordinate, longitudeCoordinate}` shape, which the detector matches out of the box.

Run it from a `Bowire.Samples` checkout (`dotnet run --project protocols/TacticalApi.RadarSweep`), point Bowire at `http://localhost:5191`, invoke `Situation.GetSituationObjects` — you'll see three pins, one per contact. Subscribe to `Situation.SubscribeSituationObjectEvents` and the pins move in real time as the server pushes fresh snapshots.

`Bundle.Workbench` ships the `Kuestenlogik.Bowire.Protocol.TacticalApi` plugin so the gRPC method names + descriptors resolve without separate proto uploads.

## Screenshot

![Map widget — TacticalApi contacts rendered on MapLibre](../images/screenshots/map-widget-pins.png)

<!-- TODO: capture map-widget screenshot — capture against the Bowire.Samples TacticalApi.RadarSweep server (http://localhost:5191) via the gRPC-Web shim. -->

## Edge cases

- **Map widget disappears on Tab ↔ Split toggle** (fixed in v2.1) — extension bootstrap was fire-and-forget, so `preferredSplitExtensionForMethod` returned null at first render. Chained `render()` onto the load promise; stamped distinct host IDs so morphdom replaces wrapper subtrees wholesale ([`a9d403f`](https://github.com/Kuestenlogik/Bowire/commit/a9d403f), [`a00b534`](https://github.com/Kuestenlogik/Bowire/commit/a00b534)).
- **Out-of-range coordinates** — values outside ±90 / ±180 are silently dropped from the map (still rendered in JSON). The detector treats them as accidental shape matches rather than valid pins.
- **Non-numeric coordinates** — string values like `"53.55"` are accepted via `parseFloat`; `null` and `NaN` are skipped.
- **Mixed shapes in one response** — the detector handles `{lat, lon}` + `{latitude, longitude}` in the same payload by matching each independently.

## Extension contract reference

The widget is a thin example of the `window.BowireExtensions.register({...})` contract:

```js
window.BowireExtensions.register({
    id: 'com.kuestenlogik.bowire.map',
    kind: 'coordinate.wgs84',
    bowireApi: '1.x',
    mount: (host, ctx) => { /* MapLibre setup */ },
    unmount: (host) => { /* teardown */ }
});
```

The .NET side is a thin shell — `PackageType=BowireExtension`, an embedded `wwwroot/js/widgets/map.js`, a discovery descriptor. Bowire serves the bundle from `/api/ui/extensions/map/bundle.js` and the workbench loads it on first use.

See [Extensions](extensions.md) for the full extension-author contract and [frame-semantics-framework](../architecture/frame-semantics-framework.md) for the detector → annotation → viewer pipeline.

## See also

- [Extensions](extensions.md) — the UI extension framework the Map widget instances
- [Compose](compose.md) — Compose responses also surface the Map tab when payloads match
- [Plugin system](plugin-system.md) — the `BowireExtension` package type
- [release notes — MapLibre extension + TacticalApi integration](../release-notes/v2.1.0.md#maplibre-extension--tacticalapi-integration-new)

---
summary: 'Design for the Bowire map-widget — UI extension API + map renderer + protocol-side position extractor.'
---

# Map widget — design

**Status:** design (v1.3.0 candidate). Not yet shipped.

The map widget makes geographic protocols (DIS, TacticalAPI, anything else with lat/lon-bearing payloads) visualise their state as points on a real map instead of just a JSON stream. It also pulls in the first **UI-extension API** in Bowire — until now the workbench has been a closed front-end. The two pieces are coupled by design: any plugin can publish position frames, and any consumer (the built-in map, or some future custom panel) can subscribe.

## Scope

In scope for the first iteration:

- **One** map widget shipped in core — Leaflet or MapLibre GL JS, both MIT, both self-hostable for offline use.
- A **position-extractor contract** on `IBowireProtocol` (or a sidecar interface) so plugins can annotate which fields in their streaming frames carry a geographic position, label, timestamp.
- **DIS** + **TacticalAPI** plugins implement the contract on day one — these are the obvious users.
- A **map tab** sits next to the existing streaming-frames pane. Toggle between text and map, or split-view. Frames continue to flow into the streaming pane; the map subscribes to a derived stream.
- **Time control**: scrub through the captured timeline of a recording session, replay the position sequence at original cadence or stepped.
- **Layer toggle**: per-protocol, per-service. A DIS PDU stream and a TacticalAPI track stream can both render; user can hide one.

Out of scope (intentional cuts to keep v1 small):

- Custom basemap configuration UI — ship with one default tile source (OpenStreetMap or Stamen), plus a config knob (`Bowire:MapTileUrl`) for offline / self-hosted tile servers.
- 3D / KML / 3D terrain.
- Drawing tools (measure, polygon, etc.).
- Symbology beyond a coloured pin + label.
- Editing positions back to the server. Pure read.

## Architecture

```text
                   ┌─────────────────────────┐
 IBowireProtocol   │                         │
 ─────────────────►│  protocol plugin        │   (DIS / TacticalAPI / future)
                   │  + IPositionExtractor   │
                   └────────────┬────────────┘
                                │ extract(frame) → MapPoint?
                                ▼
                   ┌─────────────────────────┐
                   │  /api/invoke/stream     │   per-frame, server-side
                   │  + extracted MapPoint   │
                   └────────────┬────────────┘
                                │ SSE
                                ▼
                   ┌─────────────────────────┐
                   │  bowire.js / map.js     │   workbench side
                   │  + Leaflet / MapLibre   │
                   └─────────────────────────┘
```

The extraction happens server-side (in the Bowire host) so the browser never needs to know per-protocol decoding rules. The map panel receives already-typed `MapPoint` records over the same SSE channel that carries text frames.

## Contracts

### `IPositionExtractor` (new)

Lives in `Kuestenlogik.Bowire`. Optional capability — plugins implement it only when they have a meaningful geographic projection.

```csharp
namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability for protocol plugins whose streaming frames
/// carry geographic positions. Implemented by plugins like
/// <c>Kuestenlogik.Bowire.Protocol.Dis</c> (PDU entity-state) and
/// <c>Kuestenlogik.Bowire.Protocol.TacticalApi</c> (situation-object
/// locations). When present, Bowire surfaces a Map tab next to the
/// streaming-frames pane for the service.
/// </summary>
public interface IPositionExtractor
{
    /// <summary>
    /// Try to project a single streaming frame into a <see cref="MapPoint"/>.
    /// Return <c>null</c> when the frame doesn't carry a position the
    /// caller cares about — Bowire skips those frames silently rather
    /// than logging or surfacing an error.
    /// </summary>
    /// <param name="frame">The JSON frame as emitted by InvokeStreamAsync.</param>
    /// <param name="serviceId">Service id the frame came from, useful for layer-grouping.</param>
    /// <param name="methodId">Method id ditto.</param>
    bool TryExtract(JsonElement frame, string serviceId, string methodId, out MapPoint point);
}

public readonly record struct MapPoint(
    double Latitude,
    double Longitude,
    string Label,
    DateTimeOffset Timestamp,
    string? Color = null,        // CSS hex / named colour; null = layer default
    string? IconHint = null      // protocol-supplied SVG glyph id; null = pin
);
```

Why a separate interface rather than a method on `IBowireProtocol`:

- Most plugins (REST, GraphQL, SignalR, …) have no geographic semantics. Adding a method to the core interface forces every plugin to return `null` boilerplate.
- The capability surface is consistent with existing optional interfaces (`IInlineHttpInvoker`, `IInlineWebSocketChannel`, `IInlineSseSubscriber`, `IBowireStreamingWithWireBytes`).
- Future capabilities (audio decoder, image renderer, …) follow the same pattern.

### Map-tab subscription wire

The new endpoint `/api/invoke/stream/map` is a thin SSE proxy that filters the existing stream through the plugin's `IPositionExtractor` and emits one `MapPoint` JSON per accepted frame. The browser's `EventSource` reads it like any other SSE event:

```text
event: point
data: {"lat": 53.5478, "lon": 9.9925, "label": "Crane-12", "ts": "2026-05-11T08:42:13Z"}
```

A `clear` event signals "drop all current pins" — emitted at the start of a new subscription and on user-driven reset.

## UI extension hook (foundation work)

The map widget is the first concrete consumer of a more general extension surface. Until now `bowire.js` is a single monolith with no plugin-contributed JS path. The map widget needs:

1. **Plugin-contributed JS bundles** — a path for the workbench to load additional JS chunks at startup. The chunks are loaded the same way the workbench's own per-feature JS files are concatenated into `bowire.js` (via the existing `ConcatFiles` MSBuild target). For server-side-only plugins (no JS) the hook is a no-op.
2. **A `BowireUiExtension` registration on the Bowire core endpoint side** — when a request hits `/api/ui/extensions`, the core emits the list of `{ id, script-url, label }` records that the workbench loads + renders as tabs.
3. **A `window.BowireExtensions` registry on the JS side** — the loaded chunk calls `BowireExtensions.register({ id, tab, render })`. The workbench finds the tab and mounts the render function inside the pane.

For the map widget specifically the registration looks like:

```javascript
window.BowireExtensions.register({
  id: 'map',
  tab: { icon: 'map-pin', label: 'Map' },
  appliesTo: (service, method) => service.capabilities.includes('position'),
  render: (container, { service, method, recording, live }) => {
    // mount Leaflet / MapLibre, subscribe to /api/invoke/stream/map, …
  }
});
```

`appliesTo` returns truthy → the tab shows for that service. The core decides which services have `position` capability by checking which protocols implement `IPositionExtractor` and which of their service descriptors return a non-null sample point during the initial discovery probe.

## Map library choice

Both finalists are MIT, both self-hostable for offline use, both have the same vector-tile + raster-tile flexibility:

| | Leaflet | MapLibre GL JS |
|---|---|---|
| **Footprint** | ~40 KB gz | ~200 KB gz |
| **Vector tiles** | needs plugin | first-class |
| **3D / pitch** | no | yes |
| **API simplicity** | very simple | mid-complex |
| **Last release** | active | active |
| **Offline tiles** | both — file:// or local HTTP |
| **License** | BSD-2-Clause | BSD-3-Clause |

**Pick: Leaflet.** Cheaper to bundle, easier to learn, sufficient for the v1.3.0 scope (pins + labels + time scrub). MapLibre's vector-tile + 3D advantage only pays off when we want symbology and terrain — that's a v2.x conversation. If a user needs vector tiles today they can self-host MapLibre and swap via a config knob; the `MapPoint` contract is renderer-agnostic.

## Offline mode

Bowire's no-network guarantee survives the map widget:

- The Leaflet JS chunk + CSS get bundled into `wwwroot/bowire.js` (concat target).
- Default tile URL: configurable via `Bowire:MapTileUrl` and `Bowire:MapTileAttribution`. Out of the box: leave both empty → the map renders a **blank background grid** with pins drawn over it. That's offline-safe and respects users who don't want any external HTTP fetch.
- Sample / docs reference a local tile server (`tileserver-gl` with an OSM extract) for the "with tiles" demo.

This is consistent with the rest of Bowire — every external dependency is opt-in. The map renders without internet; tiles are an enhancement.

## Recording / replay integration

`MapPoint` records are emitted into the existing streaming-frames stream alongside the JSON frame they were derived from (as a structured side-payload). The recorder already captures `responseBinary` per frame; we extend the recording-step schema with an optional `extractedPoint` field that mock-replay re-emits at the original cadence. No new file format — backwards-compatible additive change.

Implication: a recording captured today against a TacticalAPI server can be replayed against `bowire mock` tomorrow, and the map renders the same trajectory both times.

## Sample

`Bowire.Samples/Map` — a tiny embedded host that emits a stream of moving fake-ship positions over gRPC server-streaming, with both `IBowireProtocol` and `IPositionExtractor` implemented. Demonstrates the contract end-to-end without depending on a real DIS / TacticalAPI server.

## Risks + open questions

- **Capability discovery probe**: a discovery-time probe ("ask the extractor: is anything position-y here?") costs one round-trip per service. Cache it. Skip for embedded mode where the host knows.
- **Time-window control on the JS side**: scrub by frame index or by wall-clock? Wall-clock is more intuitive but requires the extractor to set a deterministic timestamp. Frame-index is always available. Probably start with frame-index, layer wall-clock on top.
- **High-frequency streams** (DIS at 30 Hz with 100 entities): naïve render loop overwhelms Leaflet. Use a leaflet-canvas or layer-batching strategy. Plan B if perf bites: render every Nth frame, configurable.
- **Coordinate systems**: assume WGS84 (lat/lon) at the contract level. DIS PDUs carry ECEF — the DIS plugin's extractor converts. TacticalAPI already speaks WGS84.
- **Map widget mount**: Bowire workbench currently has hardcoded panes (request editor, response, streaming-frames, history). Adding a "tabs above the response pane" is a UI refactor in `bowire.js`. Scoped to the same iteration but flagged as the biggest UI change.

## Phasing

| Phase | Scope | Roughly |
|---|---|---|
| **1** | `IPositionExtractor` contract + plumbing through `/api/invoke/stream/map` + DIS extractor + `Bowire.Samples/Map` | 3-4 days |
| **2** | Leaflet bundle + map tab + live point rendering + offline-safe blank-tile fallback | 4-5 days |
| **3** | Time scrub for recordings + layer toggle + recording-step `extractedPoint` field | 3-4 days |
| **4** | TacticalAPI extractor (v0.2.0 of that plugin, alongside its typed CRUD work) | 1-2 days |
| **5** | Docs + sample-site card + screenshot capture | 1 day |

Roughly **two weeks total** for v1.3.0. Each phase is independently mergeable; phase 1 can land + sit idle without phase 2 if needed.

## Cross-references

- gRPC-Web transport (v1.2.0) — DIS / TacticalAPI map demos run over native gRPC, but the map contract doesn't care which transport carried the frame.
- `Kuestenlogik.Bowire.Protocol.TacticalApi` v0.2.0 will land the position extractor (planned alongside typed CRUD).
- `Kuestenlogik.Bowire.Protocol.Dis` v1.0.3 will add the DIS-side extractor (ECEF → WGS84 conversion + PDU type filter).

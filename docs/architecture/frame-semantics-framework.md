---
summary: 'Schema-annotation layer that drives viewer + editor selection. Frames are interpreted through a tagged schema; map / image / audio / chart widgets are consumers, not protocol-coupled extensions.'
---

# Frame semantics framework

**Status:** design (v1.3.0 candidate). Not yet shipped. Supersedes the
earlier `map-widget.md` ADR — that doc proposed an `IPositionExtractor`
contract on each protocol plugin; this one moves the entire mechanism
to a transport-agnostic schema layer and treats the map widget as the
first consumer rather than the framework itself.

## Why this is not "the map widget"

The visible feature is "show coordinates on a map". The underlying
question is broader: **how does Bowire decide which viewer or editor
to mount for a given field in a payload?** A few use cases queue up
behind the map case:

- A field carries PNG bytes → image viewer.
- A field carries a WAV / Opus chunk → audio player.
- A field carries an array of timestamped scalars → chart.
- A field carries an array of records with consistent shape → grid.
- A request field is `latitude` + `longitude` paired → map *editor*
  (drag a pin, the request fields fill in).

A protocol plugin like gRPC, REST, GraphQL, SignalR, SocketIO, MQTT or
WebSocket is fundamentally **content-agnostic** — it carries opaque
payloads defined by user schemas. A `IPositionExtractor` per plugin
would either force the plugin to know every user's domain schema
(impossible) or ship blank for every transport that doesn't carry
something inherently geographic (most of them). Neither scales.

The pattern that does scale is the one pgAdmin uses: inspect the
schema, recognise a known semantic (`geometry` column → map view), and
offer the matching viewer. **Bowire generalises that to "any field
with a known semantic gets a matching viewer/editor."** The plugin
contract stays at the transport layer.

## Core model: tagged schema annotations

The single data model the whole framework runs on is a **set of
semantic annotations on schema fields**. Each annotation says "this
field, in this method, in this message type, carries semantic X."
Detectors propose annotations; users edit them; viewers and editors
consume them.

### Annotation key

```
key = (service-id, method-id, message-type-discriminator, json-path)
```

Four-dimensional, not three. The discriminator dimension matters
because **a single transport channel can carry multiple message
types**:

| Transport | Single channel, multiple shapes |
|---|---|
| DIS | One UDP/multicast channel — PDU types `EntityState`, `Fire`, `Detonation`, `Emission`, `Radio`, … |
| MQTT | One topic — envelope-tagged `{ "type": "PortCallScheduled" }` |
| SignalR | One hub stream — internal tagged-union per `eventType` |
| Kafka | One topic, multiple registered Avro/Protobuf schemas |
| Protobuf | A `oneof` field — same message, different sub-shape per tag |
| WebSocket | One socket, frames are `{ kind, payload }` |

`message-type-discriminator` is either
- `*` — Wildcard. Default for single-type methods (classical REST GET,
  most gRPC unary calls).
- A concrete value — e.g. `"EntityStatePdu"`, `1` (DIS PDU-type byte),
  `"PortCallScheduled"` (envelope `type` field value).

### Annotation value (`semantic`)

A short namespaced string identifying what the field means:

```
coordinate.latitude
coordinate.longitude
coordinate.ecef.x
coordinate.ecef.y
coordinate.ecef.z
image.bytes
image.mime-type
audio.bytes
audio.sample-rate
timeseries.timestamp
timeseries.value
table.row-array
none                    # explicit suppression — overrides upstream proposals
```

`none` is a regular annotation value, not a separate suppression
mechanism. The resolution rule is "highest-authority annotation
wins", and a user-supplied `none` wins over an auto-detector's
`coordinate.latitude`.

### Discriminator declaration

The discriminator itself is an annotation on the schema. Examples:

```yaml
# DIS plugin shipping a Discovery-Descriptor extension:
discriminator:
  wirePath: byte[1]          # before JSON decode, on the wire bytes
  typeRegistry: dis.PduType  # { 1: EntityStatePdu, 2: FirePdu, … }

# Envelope-style protocol (MQTT / WebSocket / SignalR):
discriminator:
  jsonPath: $.type           # after JSON decode

# Protobuf oneof:
discriminator:
  oneof: payload             # name of the oneof group
```

When no discriminator is declared and the user hasn't marked one, the
method is treated as single-type — discriminator value `*`.

## Annotation sources and resolution priority

Annotations can come from four sources. When two sources disagree on
the same key, the higher-priority source wins:

| Priority | Source | Where it lives |
|---|---|---|
| 1 (highest) | **User manual edit** | Right-click on a field in the response tree |
| 2 | **Plugin schema hints** | Plugin emits them in the Discovery-Descriptor |
| 3 | **Auto-detector proposals** | Bowire core scans sample frames, writes proposals |
| 4 (lowest) | (no annotation) | Field is just `scalar.number` or similar — no viewer triggered |

The user is ultimate ground truth. A `semantic: "none"` from the user
suppresses everything below.

### Auto-detector heuristics (built-in)

The auto-detector is **not** a per-frame runtime mechanism. It runs
once per discriminator-value, on a small sample of frames, and
writes proposed annotations to the schema. From then on, viewer
routing reads from the schema, not from the detector.

Default detectors shipped in Bowire core:

- **WGS84 coordinate**: paired fields whose names match
  `lat(itude)?` and `lo?ng(itude)?` (case-insensitive), with numeric
  ranges `[-90, 90]` and `[-180, 180]` respectively, at the same
  parent path.
- **GeoJSON Point**: an object with `type: "Point"` and
  `coordinates: [number, number]`.
- **Image bytes**: a byte array whose first bytes match a known image
  magic (`89 50 4E 47` for PNG, `FF D8 FF` for JPEG, …).
- **Audio bytes**: byte array with RIFF / OggS / fLaC headers.
- **Timestamp**: numeric or string fields with `*timestamp*` /
  `*time*` / `*at*` in the name and an ISO-8601-or-epoch shape.

The list is conservative. Cases the heuristic misses are exactly the
cases the user resolves with one right-click.

### Plugin schema hints

Plugins can opt in by emitting annotations in their Discovery
descriptor. Optional — a plugin that doesn't ship any hints is fully
supported by the framework, the user just does more clicking.

Plugins that ship hints out of the box (eventual):

- **Kuestenlogik.Bowire.Protocol.Dis** — discriminator declaration
  (PDU type byte), plus per-PDU-type field annotations covering
  ECEF coordinates, entity ids, etc.
- **Kuestenlogik.Bowire.Protocol.TacticalApi** — discriminator and
  WGS84 location annotations for situation objects.

### User manual annotation — shipped in v1.3.0 Phase 4

The right-click menu on any field in the response tree:

```
$.position.x       Auto-detected: coordinate.latitude

  ✓ Accept
  ↪ Reinterpret as ▸ coordinate.longitude
                     coordinate.ecef.x
                     pixel.y
                     scalar.number
                     (more…)
  ✗ Suppress  ("not a coordinate")
  📌 Persist for ▸ this session
                    this user
                    this project (bowire.schema-hints.json)

  Scope (for this and future frames):
  ● Just EntityStatePdu in this method        [default]
  ○ All message types in this method where this path exists
  ○ All message types in this method, all matching path names
```

Default scope is the narrowest one (current discriminator value only).
Cross-type propagation is opt-in.

**UI shape — popup menu, not modal dialog.** The menu floats over the
response pane via `position: fixed`, with a viewport-clamping step that
reads `getBoundingClientRect()` once after first render and shifts the
menu inward when its right or bottom edge would otherwise leave the
viewport. A modal would interrupt the user's reading flow on every
right-click; the popup keeps the response tree visible and dismisses on
Escape, click-outside, or after a successful action. Long-press on
touch devices opens the same menu (600 ms — below that the gesture
conflicts with native scroll/select).

**Companion-field suggestion — follow-up toast, not menu-internal
step.** Marking a field as `coordinate.latitude` triggers an
asynchronous walk of sibling fields under the same parent path; the
framework picks the top two numeric candidates and offers them as
single-click buttons inside an `info` toast: *"Pair with a longitude?
\[field_a] \[field_b] \[None]"*. Click → writes the companion
annotation under the chosen sibling at the same tier as the source
mark. Symmetric for `coordinate.longitude`, `coordinate.ecef.{x,y,z}`,
`image.bytes` ↔ `image.mime-type`, `audio.bytes` ↔ `audio.sample-rate`.
The suggester never blocks the menu close — failure or absence of
candidates is silent.

**Badge palette — neutral / accent-tint / accent.** Every annotated
leaf in the response tree carries a `.bowire-semantics-badge` showing
`kind (source)`. Colour-coded by source tier:
`(auto)` → neutral grey, matches the rest of the tree's secondary text;
`(plugin)` → accent-subtle background with accent foreground, marking
plugin-supplied hints; `(user)` → solid accent background, the
user-pinned annotation. Clicking a badge opens the same menu the
contextmenu event opens (same `bowireOpenSemanticsMenu` entry point).
Unannotated leaves carry no badge — clean visual.

**Persistence sticky default.** "Persist for ▸" picks the storage
tier; the choice persists across the rest of the session in
`localStorage.bowire_semantics_persist_default` (allowed values
`session` / `user` / `project`) so a power user who consistently
promotes to the project file doesn't re-pick "project" every time.
First-time use defaults to `session`.

**Scope picker — bounded by the seen-discriminators catalogue.** The
"All message types in this method where this path exists" and "all
matching path names" options would in principle be quadratic over the
schema universe. In practice the framework only writes to discriminator
values + path leaves the workbench has already streamed through — the
`bowire:stream-message` event handler updates a write-only catalogue
that the scope expander reads. A new method with a single
discriminator value seen so far translates "all message types" into
"the one type" — no work amplification on cold methods.

**HTTP endpoints.** `POST /api/semantics/annotation` writes one
annotation at the named tier and returns the resulting effective tag
(so the UI badge can update atomically). `DELETE` removes it from the
named tier and returns the *new* effective tag — important for the
cross-tier-survival semantics: deleting a user-tier `none` reveals the
auto-detector's `coordinate.latitude` beneath, and the badge changes
from "user/suppressed" to "auto/latitude" without a refresh round-trip.
Tier-disabled deployments (empty `SchemaHintsPath`, missing project
file) return 404 on the unavailable tier — graceful degrade to the
remaining tiers.

## Persistence

Three storage layers, with explicit escalation rather than implicit
sync:

| Layer | Path | Lifetime |
|---|---|---|
| Session | in-memory | Until tab close. Default — zero friction. |
| User | `~/.bowire/schema-hints.json` | Survives restart. User clicks "Remember." |
| Project | `bowire.schema-hints.json` in repo root | Team-shared, version-controlled. Explicit Export. |

Wire format (project file, identical shape for the user-local one):

```json
{
  "version": 1,
  "schemas": [
    {
      "service": "dis.LiveExercise",
      "method": "Subscribe",
      "discriminator": {
        "wirePath": "byte[1]",
        "registry": "dis.PduType"
      },
      "types": {
        "EntityStatePdu": {
          "$.entityLocation.x": "coordinate.ecef.x",
          "$.entityLocation.y": "coordinate.ecef.y",
          "$.entityLocation.z": "coordinate.ecef.z"
        },
        "FirePdu": {
          "$.locationInWorldCoords.x": "coordinate.ecef.x",
          "$.locationInWorldCoords.y": "coordinate.ecef.y",
          "$.locationInWorldCoords.z": "coordinate.ecef.z"
        }
      }
    },
    {
      "service": "harbor.HarborService",
      "method": "WatchCrane",
      "types": {
        "*": {
          "$.position.lat": "coordinate.latitude",
          "$.position.lon": "coordinate.longitude"
        }
      }
    }
  ]
}
```

`"*"` is the literal wildcard for methods with a single message type.

## Viewer / editor symmetry

A semantic annotation is **bidirectional**. The same
`coordinate.latitude` annotation drives:

| Direction | Behaviour |
|---|---|
| Response side | Map *viewer* mounts as a tab. Streaming frames render as points; their colour can be driven by the discriminator value (e.g. `EntityStatePdu` blue, `FirePdu` red). |
| Request side | Map *editor* replaces the two number-input fields. Dragging the pin updates both fields in the request payload before submit. |

This is the architectural reason "field annotations" is the right
framing, not "detector output." A detector is a one-way concept. An
annotation on a schema is two-way.

### Pairing requirement

A widget can require multiple paired annotations to mount. The map
widget needs:

```yaml
required:
  - semantic: coordinate.latitude
  - semantic: coordinate.longitude
  scope: same parent object   # $.position.lat and $.position.lon,
                              # not $.a.lat and $.b.lon
```

If only one annotation of the pair exists, the widget shows a hint
("Latitude marked, longitude needed — pick a companion field?")
rather than mounting with degraded state. Image-viewer pairs
`image.bytes` with optional `image.mime-type`. Audio-player pairs
`audio.bytes` with `audio.sample-rate`.

### Layout manager

How a viewer gets mounted is the **workbench**'s call, not the widget's.
The widget contract above only says "here is what mounts when a kind is
present"; the layout decision — tab in the response pane, side-by-side
split, future floating window — lives one layer up so the workbench can
keep the surface evolving without forcing widget authors through a
breaking-contract migration every time a new layout mode lands.

Per-kind defaults live in `wwwroot/js/layout.js → defaultLayoutForKind`:

- `coordinate.wgs84` → `split-horizontal` (list-on-left, map-on-right).
  The map needs to react to multi-selected frames in real time, which
  only works if the streaming-frames list stays visible alongside it.
- every other kind → `tab` (unchanged behaviour — the response pane
  carries the viewer next to "Response" and "Response Metadata").

The user can override the default per-(service, method, widget) tuple.
The choice persists to `localStorage` under
`bowire_widget_layout:${serviceId}:${methodId}:${widgetId}` with a
forward-compatible shape: `{ mode: 'tab' | 'split-horizontal' |
'split-vertical' | 'floating', ratio?: number }`. v1.3.1 ships
`tab` and `split-horizontal` from the toggle UI only; the other two
modes are accepted by the persistence layer so a future minor version
can light them up without a migration. `floating` (pop-out window) is
reserved for v1.x and explicitly out of scope for 3.1.

The split-pane primitive itself (`layout.js → createSplitPane`) is
deliberately widget-agnostic — vanilla JS, two named slots, one
draggable divider, ratio persisted under a caller-supplied
localStorage key, mobile-fallback to a vertical stack below 720px.
The divider drag handler attaches `mousemove` / `mouseup` to the
`document` only for the duration of a single drag and detaches both
on `mouseup`, so a `render()` that tears down and rebuilds the split
pane never leaks handlers across recreates.

### Layer behaviour for mixed discriminators

When several discriminator values in the same method produce frames
matching the same widget (e.g. DIS stream emitting both `EntityState`
and `Detonation`, both annotated with `coordinate.ecef.*`), the
default rendering is **one widget, one layer per discriminator
value**. Layer-toggle in the widget UI lets the user hide individual
types. Two separate tabs is the explicit alternative when the
visualisations are semantically very different.

## Lifecycle of a binding

```
1. User opens a service+method for the first time.
2. Bowire probes (in order):
   - existing user-supplied annotations (from session/user/project)
   - plugin-supplied schema hints from Discovery descriptor
   - auto-detector against a small sample of frames
3. Each newly-classified field appears in the response tree with a
   semantic badge: "coordinate.latitude (auto)" or "(plugin)".
4. User can accept / redirect / suppress per field.
5. Effective schema is settled. Widget(s) mount if pairing
   requirements are met.
6. All subsequent frames + request edits route through the fixed
   binding — no re-detection per frame.
7. New message-type discriminator seen → auto-detector runs once
   for that new type, repeats from step 3 for new (type, path)
   pairs. Already-classified pairs are NOT re-evaluated.
8. Session-end or explicit "Reset annotations" clears the
   in-memory layer. Persisted layers (user / project) survive.
```

Step 7 — "learn as new types arrive" — is the key correctness
property for sparse streams. A `FirePdu` that arrives once per minute
gets classified the first time it shows up, not at initial subscribe.

## Map widget — the first viewer/editor

The framework's first concrete consumer. Ships in its own NuGet package
**`Kuestenlogik.Bowire.Extension.MapLibre`** (Phase 3-R — extracted out
of core in the v1.3.0 pre-release refactor so users who never invoke a
coordinate-carrying method don't pay the ~870 KB bundle cost). The
package follows the standard extension model — `[BowireExtension]`
descriptor + embedded JS/CSS/vendor assets — and is the dogfood proof
that the same shape third parties write against is the shape Bowire's
own widgets use. Install via `dotnet add package
Kuestenlogik.Bowire.Extension.MapLibre`; the workbench's
`/api/ui/extensions` enumeration auto-discovers it at boot, dynamic-
loads the bundle via `bowireLoadExternalExtensions`, and registers the
viewer + editor against `coordinate.wgs84`. Implementation choices:

### Map library: MapLibre GL JS

`maplibre-gl@4.x`, BSD-3-Clause. **Not Leaflet**. The earlier draft
defaulted to Leaflet on a "smallest dependency" reflex; in context
that was wrong.

- **Performance**: WebGL renderer handles ~10⁴ features without
  fighting the DOM. Leaflet's SVG/DOM model needs Canvas-plugin
  workarounds past ~10³. DIS at 30 Hz × 100 entities = ~3000
  feature updates per second — exactly the regime where Leaflet
  forces an architecture detour.
- **Vector tiles** are first-class. The 2026 baseline for any
  serious tile source (Protomaps, MapTiler, OSM-vector) is vector.
  Leaflet needs `Leaflet.VectorGrid` for the same thing.
- **Symbology**: sprite-atlas is first-class — when DIS / TacticalAPI
  layers want ship-vs-aircraft-vs-ground distinctions, MapLibre has
  the data model for it without per-icon URL juggling.
- **Bundle**: ~200 KB gz vs. Leaflet's ~40 KB. For a developer tool
  that loads once per session, this is a non-trade-off.
- **License**: BSD-3-Clause, compatible with Bowire's Apache-2.0.

Default tile source: **Protomaps** (free, OSM-based, single-file
`.pmtiles` self-host-friendly). Online mode pulls from
`https://api.protomaps.com/tiles/v3/{z}/{x}/{y}.mvt`. Offline mode
(no `Bowire:MapTileUrl` configured) renders pins on a blank grid
background — same offline-safe behaviour as the earlier draft.

### Subscription wire

No separate `/api/invoke/stream/map` endpoint. The existing
`/api/invoke/stream` SSE channel is **enriched**: each frame event
already carries the decoded JSON; when the schema has annotations
that downstream widgets care about, the same event additionally
carries an `interpretations` field:

```text
event: frame
data: {
  "frame": { ...the decoded payload as today... },
  "discriminator": "EntityStatePdu",
  "interpretations": [
    { "kind": "coordinate.wgs84",
      "path": "$.position",
      "lat": 53.5478, "lon": 9.9925 }
  ]
}
```

One SSE connection per method (as today), interpretations are an
additive side-channel. Recordings persist the `interpretations`
field alongside frames; replay re-emits them at the original cadence
without re-running detection.

## Offline mode

Bowire's no-network guarantee survives:

- MapLibre JS + CSS ship as embedded resources of the
  `Kuestenlogik.Bowire.Extension.MapLibre` NuGet package
  (Phase 3-R — moved out of core for the bandwidth win). Same-origin
  served via `/api/ui/extensions/kuestenlogik.maplibre/{name}`. Users
  who install the package pay the ~870 KB asset cost once at first
  map mount; users who don't install it never see the bundle at all.
- Default behaviour without `Bowire:MapTileUrl`: blank grid + pins
  on top. Renders correctly with zero outbound HTTP.
- Sample / docs reference a local Protomaps single-file
  (`tileserver-gl` style) for the "with tiles" demo.

**Glyph / sprite lockdown (Phase 3-R).** The MapLibre style declared by
the widget intentionally omits both the `glyphs` URL and the `sprite`
URL — regardless of whether `Bowire:MapTileUrl` is set. MapLibre only
fetches glyph PBFs when a `symbol` layer with a `text-field` paint
property is mounted, and only fetches sprite atlases when an
`icon-image` layer is mounted; the widget renders pins through the
`circle` layer primitive (per-discriminator colour, multi-select
restyle) which needs neither. A regex-over-bundle CI test pins the
absence of both fields plus the absence of `text-field` / `icon-image`
references — any future style tweak that re-introduces an outbound
glyph or sprite fetch fails the build before the no-network guarantee
leaks. When the user does set a tile URL, the tile fetch is the only
external request the workbench ever makes against that origin.

The framework itself has no network dependency — tile downloads
are an optional enhancement, not a requirement.

## Recording / replay integration — shipped in v1.3.0 Phase 5

The recording-step schema gains two **additive, optional** fields:

```json
{
  "step": 42,
  "request": { ... },
  "response": { ... },
  "responseBinary": "...",
  "discriminator": "EntityStatePdu",
  "interpretations": [
    { "kind": "coordinate.wgs84",
      "path": "$.position",
      "payload": { "lat": 53.5478, "lon": 9.9925,
                   "latPath": "$.position.lat",
                   "lonPath": "$.position.lon" }
    }
  ]
}
```

Recordings made under earlier Bowire versions (no `discriminator`,
no `interpretations`) replay unchanged — the framework re-runs
detection on load. Recordings made with v1.3+ replay deterministically
even if detector heuristics drift between versions.

**`RecordedInterpretation.Payload` shape conventions.** The payload is
deliberately a free-form `JsonElement` so the framework doesn't freeze
itself into one widget's shape this early. For built-in kinds:

- **`coordinate.wgs84`** — `{ "lat": number, "lon": number, "latPath": "...", "lonPath": "..." }`. Lat/lon inlined as numbers so the map widget reads directly without re-resolving JSONPaths; the per-axis paths round-trip for round-trip-fidelity (the editor uses them to write back into the request).
- **`image.bytes`** — `{ "data": "<base64>", "mimeType": "image/png"?, "bytesPath": "...", "mimePath": "..."? }`. The `mimeType` and `mimePath` keys appear only when an `image.mime-type` companion annotation pairs with the bytes at the same parent.
- **`audio.bytes`** — `{ "data": "<base64>", "sampleRate": 44100?, "bytesPath": "...", "ratePath": "..."? }`. Same companion-optional shape.

Unknown / unhandled semantic kinds are silently skipped at capture
time — they round-trip through the schema sidecar (see below) but
don't emit per-frame interpretations until a future phase wires
their payload extraction.

**Effective-schema sidecar.** A frozen snapshot of the effective
annotation set at record-time lives at the top of the recording file
alongside the existing metadata:

```json
{
  "recordingFormatVersion": 2,
  "recordedAt": "...",
  "schemaSnapshot": {
    "annotations": [
      { "service": "harbor.HarborService",
        "method": "WatchCrane",
        "messageType": "*",
        "jsonPath": "$.position.lat",
        "semantic": "coordinate.latitude" },
      { "service": "harbor.HarborService",
        "method": "WatchCrane",
        "messageType": "*",
        "jsonPath": "$.position.lon",
        "semantic": "coordinate.longitude" }
    ]
  },
  "steps": [ ... ]
}
```

The sidecar is what the workbench uses to mount widgets when a recording
is opened — so a recording made against one user's annotations renders
identically for another user whose local annotations differ. **v1-vs-v2
file detection is by presence of `schemaSnapshot`, not by bumping the
file-format version field.** `recordingFormatVersion` stays at `2`
(set by the Phase-1b gRPC-binary work); the loader treats the field's
absence as "ask the live annotation store at replay time" — strictly
backwards-compatible with every v1 / v2-pre-Phase-5 recording on disk.
Explicit `"none"` suppression entries round-trip unchanged so a
user-tier `none` replayed against a different store still suppresses
the lower-tier proposal.

**`RecordingReplayInterpretationResolver` — short-circuit semantics.**
The replay path consults this helper per emitted frame. When the step
carries `interpretations` the helper hands the list back verbatim and
skips the prober entirely — captured payloads win regardless of how
detector heuristics have drifted since record-time. When the step
lacks the field (pre-Phase-5 recordings), the helper runs the live
`IFrameProber` and `RecordingInterpretationBuilder` as a fall-back, so
older recordings replay through detection exactly as v1.2 did. The
prober's `ProbedTripleCount` is the test seam that verifies the
short-circuit: a v2 recording (interpretations captured) never ticks
the counter on replay.

**Sample.** `Bowire.Samples/SchemaSemantics` — a deliberately
plain-vanilla gRPC server-streaming method that emits
`{ ship, lat, lon, status }` frames. No `IBowireSchemaHints`
implementation, no plugin awareness, no manual user annotation file
shipped — the pgAdmin proof: the framework finds the coordinates by
content alone and the map mounts automatically.

## Out of scope for v1.3

- **Cross-method propagation**: marking `$.coords.lat` in method A
  does not automatically apply to method B. Strict scope is
  `(service, method, type, jsonPath)`. A future "promote to
  project-wide pattern" UI is a v1.4 candidate if real usage shows
  the need.
- **3D map**: pitch/bearing are MapLibre primitives but the v1
  widget renders 2D only. Adding 3D is a styling change, not an
  architectural one.
- **Drawing tools**: measure, polygon, draw — not in v1.3.
- **Custom tile-source UI**: `Bowire:MapTileUrl` config knob only.
- **Audio / image / chart / grid viewers**: framework supports them,
  but only the map viewer ships in v1.3.0. The first additional
  viewer follows in v1.4.

## Extension framework

The framework above only solves the *built-in* viewer/editor case.
The companion problem is: **how does a third party ship a new viewer
or editor without forking Bowire core?** Map / image / audio / chart /
grid are not exhaustive — users will eventually want a MIL-STD-2525
symbol viewer, a hex viewer for embedded-systems debugging, a
Mermaid-diagram renderer for `text/mermaid` payloads, a Protobuf-wire-
bytes inspector, a 3D-point-cloud viewer, … . Baking all of those into
core is the wrong direction; the extension surface is.

Bowire already has an extension model that works: protocol plugins
ship as separate NuGet packages, auto-discover via assembly scan, and
register against a stable contract. Viewer/editor/detector extensions
get the same model — only a different package type.

### Packaging and loading

```
NuGet package
└── Kuestenlogik.Bowire.Extension.MapLibre
    ├── csproj with <PackageType>BowireExtension</PackageType>
    ├── BowireMapLibreExtension.cs        — C# registration + metadata
    └── EmbeddedResource: bundle.js + bundle.css
                                          — JS implementation, served
                                            from the local Bowire host
```

Loading sequence at workbench startup:

1. Host assembly-scans for types attributed with `[BowireExtension]`
   (mirroring `[BowireProtocol]` discovery today).
2. Workbench calls `GET /api/ui/extensions` → JSON list of
   `{ id, version, bowireApi, kinds, capabilities, bundleUrl, stylesUrl? }`.
3. Per extension, the workbench dynamically imports the bundle URL.
   The URL serves the embedded resource from the local Bowire host
   — **never from a third-party CDN**. Offline-safe by construction.
4. The bundle calls `window.BowireExtensions.register({...})` with
   its registration record (see JS contract below).
5. The annotation → widget routing now treats the new `kind` as
   mountable. Existing screens (response panes, request forms)
   pick up the new viewers/editors without any core code change.

The built-in viewers (map, image, audio, table) ship as regular
`BowireExtension` packages — **not bundled with Bowire core**. After
Phase 3-R the only thing core ships is the framework itself
(`extensions.js`, the `/api/ui/extensions` endpoint, the placeholder-
tab path); every viewer/editor — including the MapLibre map — is a
separate NuGet package the user explicitly installs. Same code path
third-party widget authors write against; gap in the API surfaces
immediately during Bowire's own development.

**Placeholder tab for unregistered kinds (Phase 3-R).** When the
workbench sees an annotation kind for which no extension has registered
— for example `coordinate.latitude` from the auto-detector but
`Kuestenlogik.Bowire.Extension.MapLibre` is not installed — the
framework mounts a generic placeholder card in the viewer slot:
`Install Kuestenlogik.Bowire.Extension.MapLibre to render
\`coordinate.latitude\` annotations on a map.` The package id is
copy-to-clipboard-able next to the message; Bowire core cannot install
NuGet packages from the workbench, so the card is informational only.
The placeholder is generic across kinds: a `kind → packageId`
suggestion table in `extensions.js` plugs new entries in additively as
new built-in extensions ship. The card suppresses itself when the
kind is covered by an already-registered extension's
`pairing.required` list (so MapLibre being installed makes the
companion-kind cards disappear), and dedupes by suggestion id so a
method that surfaces both `coordinate.latitude` and
`coordinate.longitude` doesn't double-render the MapLibre card.

### C# contracts

```csharp
[BowireExtension]
public sealed class MapLibreExtension : IBowireUiExtension
{
    public string Id => "kuestenlogik.maplibre";
    public string BowireApiRange => "1.x";          // semver range
    public string[] Kinds => ["coordinate.wgs84"];
    public ExtensionCapabilities Capabilities
        => ExtensionCapabilities.Viewer | ExtensionCapabilities.Editor;

    // Embedded resources, served at /api/ui/extensions/{Id}/{name}:
    public string BundleResourceName => "bundle.js";
    public string? StylesResourceName => "bundle.css";

    // Future, post-v1.0: required permissions, declared kinds,
    // dependent extensions. Empty in v1.0 — most extensions need none.
}
```

Server-side detectors register through a sibling contract — same
attribute, different interface:

```csharp
[BowireExtension]
public sealed class MilSymbolDetector : IBowireFieldDetector
{
    public string Id => "kuestenlogik.milsymbol-detector";
    public string BowireApiRange => "1.x";

    public DetectionResult? Detect(FieldContext ctx) =>
        ctx.Name.EndsWith("Sidc", StringComparison.Ordinal) &&
        ctx.Value is string s && Sidc.IsValid(s)
            ? DetectionResult.Propose("mil.symbol-code")
            : null;
}
```

A single NuGet package can ship multiple `[BowireExtension]` types
together — the MapLibre extension ships its UI extension; a future
MIL-symbol package would ship a detector *and* a viewer in the same
nupkg.

### JS contract — `BowireExtensions.register`

This is the API that becomes a public commitment once published.
v1.0 is deliberately tight; everything that doesn't need to ship in
v1.0 waits for v1.1 to avoid baking premature decisions into a
contract we can't break.

**v1.0 `ctx` surface** (minimal, evolves additively):

```javascript
window.BowireExtensions.register({
  id: 'kuestenlogik.maplibre',
  bowireApi: '1.x',
  kind: 'coordinate.wgs84',

  pairing: {
    required: ['coordinate.latitude', 'coordinate.longitude'],
    scope: 'same-parent'   // 'any' | 'same-parent' | 'same-object'
  },

  viewer: {
    label: 'Map',
    icon: 'map-pin',
    selectionMode: 'multi',   // Phase 3.2 — 'single' (default) | 'multi'
    mount(container, ctx) {
      // ctx.frames$       — async iterable of { frame, interpretations,
      //                     discriminator } events on the response stream
      // ctx.selection$    — async iterable of { selectedFrameIds }
      //                     SNAPSHOTS (Phase 3.1, see below). Filtered
      //                     to [lastSelected] when selectionMode is
      //                     'single' (Phase 3.2).
      // ctx.theme         — { mode: 'light' | 'dark', accent, font }
      // ctx.viewport      — { width, height, on(event, cb) → unsubscribe }
      // ctx.host          — { subscribeSse(url), fetch(url, init) }
      //                     fetch uses Bowire's auth + CSP context;
      //                     subscribeSse closes itself on unmount.
      // returns: () => void   — unmount cleanup
    }
  },

  editor: {
    label: 'Pick on map',
    selectionMode: 'single',  // Phase 3.2 — default
    mount(container, ctx) {
      // ctx.value         — { 'coordinate.latitude': 53.5, … }
      //                     (paired annotation kinds → current values)
      // ctx.onChange(p)   — patches the request payload with `p`
      // ctx.disabled      — boolean (e.g. during request execution)
      // ctx.theme / ctx.viewport / ctx.host — same as viewer side
      // returns: () => void
    }
  }
});
```

**`selection$` — snapshot stream, not deltas (Phase 3.1).** The workbench
broadcasts the current set of user-selected frames every time the
Streaming-Frames pane's selection changes. Each event the widget pulls
from `ctx.selection$` carries the **complete** `{ selectedFrameIds:
ReadonlyArray<string|number> }` snapshot — never a "+id" / "-id" delta.
This means a viewer never has to accumulate state to know what's
selected; the first awaited value IS the current selection, and every
subsequent value IS the new authoritative state. As a corollary, a
widget that mounts AFTER the user already made a selection (e.g. they
selected three frames, then switched layout from tab to split,
spinning up a fresh map) gets the current snapshot on its first
`await` — no manual rehydration needed. The transport is a custom
`bowire:frames-selection-changed` DOM event the workbench dispatches
exactly once per logical change (so N selected frames produce one
N-entry snapshot, never N delta events). Frame ids on the snapshot are
the same `${service}/${method}#${index}` keys the workbench mints when
each frame arrives over the SSE stream.

**`selectionMode` — per-block capability declaration (Phase 3.2).** A
widget that can render only one selected item (a key-value detail view,
a single-coordinate editor) declares `selectionMode: 'single'` on its
viewer / editor block; widgets that can sensibly render N selected
items (the map, a future timeseries chart) declare `'multi'`. This is
a **capability declaration, not a user-facing setting** — the user
still controls *which* items end up selected; the widget controls
*whether* the framework lets it see the full set. When a `'single'`
widget is mounted, the framework wraps `ctx.selection$` so each yielded
snapshot is truncated to `[lastSelected]` (the most-recently-added id
vs. the previous snapshot; on multi-add events like select-all, the
last entry in array order); empty snapshots stay empty. `'multi'`
widgets receive the snapshot unfiltered. Default is `'single'` —
conservative for new widget authors who haven't yet thought about
multi-select. The field is per-block so a viewer can be `'multi'`
while the same widget's editor is `'single'`; per-ctx filter state
keeps two widgets on the same method independent. Built-in defaults:
**Map viewer → `'multi'`** (the existing fitBounds-the-selected-set
camera + restyle logic ships in Phase 3.1), **Map editor → `'single'`**
(one coordinate pair at a time). Validation at `register({...})` time
rejects any value other than `'single'` / `'multi'` with a
`console.warn` and drops the offending block — no silent coercion.

Deliberately **not** in v1.0:
- Recording playback controls (`currentStep`, `setStep`, `totalSteps`).
  Recordings still display, but the scrub-control surface is added
  in v1.1 once we know what extensions actually need.
- Cross-extension messaging. A v1.1 concern if real demand emerges.
- Persistence helpers for per-extension state. Extensions can use
  `localStorage` for now; first-class store comes later if needed.

Everything in `ctx` is **frozen for the v1.x lifetime**. Adding
fields is allowed in minor versions; removing or repurposing
existing fields is a v2.x change.

### Versioning and compatibility

- Extension declares `bowireApi: '1.x'` (semver range).
- Workbench checks compatibility on load. Incompatible major → the
  extension is skipped with a UI badge "needs Bowire 2.x" in the
  Extensions marketplace tab, no silent failure.
- Bowire core MUST remain backwards-compatible within a major. That
  discipline is the reason `ctx` ships tight in v1.0 — every field
  added to the contract is something we can't take back without
  a breaking release.

### Permissions model

Categorical for v1.0, with an explicit user opt-in moment:

- Default: extension runs inside the workbench origin, can render
  arbitrary DOM, can subscribe to Bowire-internal streams via
  `ctx.host.subscribeSse`. Cannot make outbound HTTP fetches outside
  the Bowire origin (CSP-blocked at the response level).
- An extension that needs an outbound fetch (e.g. a tile-server
  consumer for the map widget) declares it in its registration:
  ```javascript
  permissions: ['fetch:protomaps.com/*']
  ```
- On first load of such an extension, the workbench prompts the user
  once: "*Extension kuestenlogik.maplibre wants to fetch tiles from
  api.protomaps.com. Allow? [Just this session / Always / Never]*".
  The choice is persisted per-extension in `~/.bowire/extensions.json`.

Two-class model: zero-permission extensions install silently, network-
hungry extensions go through a one-time consent. Granular per-resource
permissions (e.g. read-only vs. read-write tile cache) are a v1.x
additive surface if the categorical model proves too coarse — but
shipping coarse-first means most extensions never trigger a prompt,
which is what users actually want.

### Extension conflict resolution

Two extensions can register for the same `kind`. Resolution rule:

1. **Default**: the built-in extension wins. The user's installed
   third-party extension still appears in the Extensions tab, but
   isn't auto-mounted.
2. **Per-binding override**: the response pane has a viewer-picker
   dropdown when more than one extension is registered for the
   active `kind`. Choosing a non-default extension persists the
   choice in the same `bowire.schema-hints.json` file that carries
   the annotation, under a `viewer` companion field:
   ```json
   {
     "service": "harbor.HarborService",
     "method": "WatchCrane",
     "types": {
       "*": {
         "$.position.lat": "coordinate.latitude",
         "$.position.lon": "coordinate.longitude"
       }
     },
     "viewers": {
       "coordinate.wgs84": "acme.super-map"
     }
   }
   ```
3. The viewer override has the same three persistence tiers as the
   annotations themselves (session / user / project). Default scope
   is the narrowest one.

No registry-wide priority ordering, no automatic version-based
preference — explicit user choice every time. Avoids the "why is
this random extension rendering my data?" surprise; matches the
pattern users already learn from the annotation system itself.

### Cross-extension dependencies

An extension can introduce a new kind that other extensions consume.
Bowire maintains a runtime kind registry:

- Built-in kinds (`coordinate.wgs84`, `coordinate.ecef`,
  `image.bytes`, `audio.bytes`, `timeseries.value`,
  `table.row-array`, …) are registered by the core.
- Extension kinds are declared at `register(…)` time:
  ```javascript
  declareKinds: ['mil.symbol-code', 'mil.echelon']
  ```
- Dependent extensions list their required kinds. On load, the
  workbench checks the registry; if a required kind is missing, the
  extension shows a disabled state in the Extensions tab with
  "requires `acme.utm-detector`". Lazy resolution — when a missing
  kind later becomes available (another extension loaded), the
  disabled extension is rebound without restart.

### Marketplace UI

A dedicated tab in the workbench:

- **Installed**: built-ins (map, image, audio, table) on top,
  third-party below. Per-row: id, version, status (active /
  blocked-permission / blocked-bowire-version / blocked-dependency),
  link to source repo.
- **Discover**: nuget.org query with
  `packageType:BowireExtension`. Click → install:
  - **Embedded mode**: writes a `<PackageReference>` to the host
    project's csproj, runs `dotnet restore`, prompts for an app
    restart.
  - **Standalone mode**: downloads the nupkg to
    `~/.bowire/extensions/{id}/`, the assembly is loaded on the
    next bowire start.
- **Permissions**: per-extension toggle for any granted permissions.
  Revoking is immediate; the extension is re-prompted on next use.

### Scaffolding

`dotnet new bowire-extension --kind audio.opus --name acme.opus-player`
creates:

- csproj with `<PackageType>BowireExtension</PackageType>` and the
  correct `<EmbeddedResource>` wiring for the JS bundle.
- C# registration class implementing `IBowireUiExtension` with
  pre-filled `Id` / `Kinds` / `Capabilities`.
- JS bundle stub with `register({...})` skeleton and a TypeScript
  `.d.ts` for the v1.0 `ctx` API surface.
- README with the publish-to-nuget.org checklist.
- GitHub Action template covering build → pack → push, matching
  the release pipeline already used by Bowire's own plugin repos.

Without scaffolding the boilerplate-cost is high enough to deter
authors. The template is what turns "you *could* write an extension"
into "you'll spend an evening on it."

### Plugin author tax for protocol plugins

Protocol plugins (gRPC, REST, MQTT, …) keep their existing contract
unchanged. The framework above only *adds* optional surfaces — none
of them are mandatory:

- Schema annotations in Discovery descriptor: optional, lets the
  plugin pre-fill annotations for known field shapes (e.g.
  TacticalAPI tagging location fields as `coordinate.wgs84`,
  DIS declaring its PDU-type discriminator). Skipping it means
  users do more right-clicking, nothing breaks.
- Bundled viewer/editor: optional. A protocol plugin can ship its
  own `[BowireExtension]` if its payloads have a non-generic shape
  worth a dedicated visualisation. Most won't.
- Custom detector: optional, server-side rule that proposes
  annotations the built-in heuristic can't reach.

The tax for "my protocol now supports the map widget" is zero if
the schema uses conventional field names, one schema-hint file
entry if it doesn't.

## Risks and open questions

- **Discriminator unknown at first**: many real protocols don't
  declare their discriminator in any machine-readable way. Bowire
  falls back to single-type (`*`) until the user marks a
  discriminator field. The right-click menu offers
  "this is the type discriminator (cardinality looks low)".
- **Pair-without-parent**: `$.lat` and `$.lon` at root, but the
  protocol nests them as `$.geometry.lat` and `$.coords.lon`. The
  pair-at-same-parent heuristic misses it. User resolves by
  marking both manually.
- **Annotation file conflicts**: project-shared
  `bowire.schema-hints.json` versioned alongside code; users with
  diverging user-local hints get a deterministic merge (project
  wins for any key where both sides have a value, otherwise
  user-local fills in). No interactive merge UI in v1.3.
- **Large annotation files for chatty schemas**: a service with 200
  methods × 5 types × 10 fields = 10,000 entries. The file is still
  small (sub-megabyte JSON) and is loaded once at startup.
- **Detector versioning vs. live streams**: tightening heuristics in
  v1.4 could re-classify a live stream differently from how v1.3
  did. Persisted recordings are immune (they carry their own
  interpretations). Live behaviour can shift between major
  versions; documented as a breaking-change class.

## Phasing

| Phase | Scope | Rough effort |
|---|---|---|
| **1** | Annotation data model + storage (session/user/project) + Discovery-Descriptor extension for plugin hints + resolution priority resolver | 4–5 days |
| **2** | Built-in detectors (WGS84 coordinate, GeoJSON Point, image magic, audio header, timestamp) + sample-frame probe + learn-as-new-types loop | 3–4 days |
| **3** | MapLibre bundle + map viewer registered on `coordinate.wgs84` + offline-safe blank-tile fallback + pairing logic | 4–5 days |
| **4** | Manual user override UI (right-click → mark / redirect / suppress) + companion-field suggestion + scope picker | 4–5 days |
| **5** | Recording `interpretations` + replay determinism + sample-site demo (`Bowire.Samples/SchemaSemantics`) + docs | 2–3 days |

~3 weeks total for v1.3.0. Each phase is independently mergeable;
phase 1+2 (the data model and detectors) can ship without phase 3 if
the map widget needs to lag.

## Sample

`Bowire.Samples/SchemaSemantics` — a deliberately plain-vanilla
gRPC service that streams `{ ship, lat, lon, status }` frames. No
position-extractor, no plugin awareness, no custom viewer. Demonstrates
that the framework finds the coordinates by content, the map mounts
automatically, and the user has no extra work to do. The pgAdmin
proof.

A second variant within the same sample exposes a `oneof` payload
with `PositionUpdate` and `StatusBroadcast` cases — exercises the
discriminator path with protobuf-native semantics.

## Cross-references

- gRPC-Web (v1.2.0) — orthogonal; the framework operates on decoded
  frames and doesn't know which transport delivered them.
- `Kuestenlogik.Bowire.Protocol.TacticalApi` v0.2.0 will ship the
  position-extractor schema hint (alongside its typed CRUD work).
- `Kuestenlogik.Bowire.Protocol.Dis` v1.x will ship the
  PDU-type-discriminator declaration and ECEF coordinate
  annotations.

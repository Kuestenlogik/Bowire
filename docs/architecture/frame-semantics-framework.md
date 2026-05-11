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

### User manual annotation

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

The framework's first concrete consumer. Implementation choices:

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

- MapLibre JS + CSS are bundled into the workbench JS chunk (concat
  target — same mechanism as morphdom today).
- Default behaviour without `Bowire:MapTileUrl`: blank grid + pins
  on top. Renders correctly with zero outbound HTTP.
- Sample / docs reference a local Protomaps single-file
  (`tileserver-gl` style) for the "with tiles" demo.

The framework itself has no network dependency — tile downloads
are an optional enhancement, not a requirement.

## Recording / replay integration

The recording-step schema gains two **additive, optional** fields:

```json
{
  "step": 42,
  "request": { ... },
  "response": { ... },
  "responseBinary": "...",
  "discriminator": "EntityStatePdu",
  "interpretations": [
    { "kind": "coordinate.wgs84", "path": "$.position",
      "lat": 53.5478, "lon": 9.9925 }
  ]
}
```

Recordings made under earlier Bowire versions (no `discriminator`,
no `interpretations`) replay unchanged — the framework re-runs
detection on load. Recordings made with v1.3+ replay deterministically
even if detector heuristics drift between versions.

The effective schema annotations active at record-time are also
captured as a sidecar at the top of the recording file, so a
replayed recording shows the same widgets that the live session
showed, even if the user's local annotations have changed since.

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

## Plugin author surface

Plugins keep their existing contract. New, **all optional**:

- **Schema annotations in Discovery descriptor** — a plugin that
  knows its payloads carry coordinates (TacticalAPI) or has a
  discriminator (DIS, MQTT-envelope-aware plugins) can pre-populate
  the annotation set. Skipped plugins still work, users just do
  more clicking.
- **Custom viewer or editor bundles** — a plugin can ship its own
  JS bundle that registers an additional viewer on a `kind` string
  not covered by Bowire's defaults. Loaded through the existing
  workbench JS concat path. No-JS plugins are unaffected.
- **Custom detector** — a plugin can register a server-side
  `IFieldDetector` that adds proposals for fields the core
  heuristic can't reach. Same as a hint, but rule-based instead
  of static.

The plugin author tax for "my protocol now supports the map widget"
is zero if the schema uses conventional field names, one schema-hint
file entry if it doesn't.

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

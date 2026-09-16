---
title: <fill in before the tag>
version: 2.8.0
---

<One-sentence frame for what 2.8 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern —
note the example heading is indented so the placeholder detector in
release.yml (which counts lines matching `^### `) does not mistake this
template for a filled-in body:

  ### <headline> (#issue)
  <2-4 sentences>
-->

### The map draws the symbol the code names

A pin that carried a MIL-STD-2525 symbol code was drawn as one of four coloured shapes — friend, hostile, neutral, unknown — which told the operator whose it was and nothing else. The map now renders the code itself with [milsymbol](https://github.com/spatialillusions/milsymbol) (MIT, vendored next to MapLibre; still no CDN): a cruiser is a cruiser, a fixed-wing is a fixed-wing, and the affiliation is in the frame the way the standard draws it. Both string forms are read, the fifteen-letter 2525C code and the twenty-digit 2525D one. The four shapes stay as the fallback: for the moment before the library has loaded, for a code it cannot draw, and for pins that have no code at all.

Found on the way: the pattern that picked SIDCs out of a frame checked the status letter one position late, against the first letter of the function id. Codes like `SFGPU…` passed by luck; a cruiser (`SFSPCLCC…`) did not, and a pin whose own code fell through picked up the nearest neighbour's colour from the scan of the whole frame.

### The map draws the tactical graphics too

A pin is a point, and a point is the one shape a symbol renderer draws from the code alone. Everything else MIL-STD-2525 calls a tactical graphic — a boundary, a phase line, an assembly area, an axis of advance, an air corridor, a range fan — is drawn from its geometry, and a frame carries that geometry as several coordinates, which the map used to show as several pins with the same symbol. The widget now reads the vertices back together from the paths they arrived under (an array of points under an entity with a code is one graphic; three named points around a centre are an ellipse; a vertex with ranges and azimuths beside it is a fan) and hands each geometry to [mil-sym-ts](https://github.com/missioncommand/mil-sym-ts) (Apache-2.0, 2525D / 2525E / APP-6D; vendored, no CDN), which draws it for the current view — and again whenever the camera comes to rest, because an arrowhead is so many pixels wide, not so many metres. Labels are sprites, since the offline lockdown allows no glyph server; a light casing keeps a black friendly graphic legible on satellite imagery. Until the library has landed, or for a code it refuses, the geometry is drawn bare in the affiliation colour — the graphics' counterpart to the pins' four shapes. The twenty-digit 2525D code is also read as the two ten-digit halves TacticalAPI carries it in.

The library is fetched only once a frame actually carries a geometry, and it is vendored trimmed and gzipped: `scripts/vendor/mil-sym-ts.mjs` cuts the upstream build's single-point icon tables — six megabytes of unit and equipment icons milsymbol already draws — down to the control-measure icons a few multipoint graphics carry, proves in Chromium that every control measure still renders identically, and writes the 0.6 MB that goes over the wire. The extension asset endpoint (`/api/ui/extensions/{id}/{name}`) serves an asset declared with a `.gz` suffix under its plain leaf — passed through with `Content-Encoding: gzip` for a client that accepts it, inflated for one that does not, `Vary: Accept-Encoding` on both — so the `Kuestenlogik.Bowire.Map` package carries what the wire carries. The TacticalAPI sample gained an overlay of seven control measures, one per `SymbolLocation` case, as the data to see this against.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

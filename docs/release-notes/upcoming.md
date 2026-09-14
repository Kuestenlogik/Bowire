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

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

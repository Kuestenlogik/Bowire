---
title: Rail strip
summary: 'The 48 px icon column at the very left edge that switches rails. Visual reference — icon set, active/inactive states, group separators, the bottom settings/theme cluster, the sidebar splitter, and the per-rail sub-tab strip.'
---

# Rail strip

The **rail strip** is the narrow icon column at the very left edge of the
workbench — the outermost of the two left-hand layers (the [sidebar](sidebar.md)
list panel sits directly to its right and reshapes per rail). The strip itself
stays put; clicking an icon swaps which rail is active.

This page is the **visual reference**. For how rails are loaded, enabled,
reordered, and deep-linked, see the feature page
[Rail strip](../features/rail-strip.md); for each rail's data flow and the
hand-offs between them, see [rail pipelines & hand-offs](../architecture/rail-pipelines.md).

## Anatomy

| Element | Behaviour |
|---|---|
| **Icon column** | 48 px wide. One icon button per loaded + enabled rail, ordered by each rail's `SortIndex`. |
| **Active icon** | Rendered at full opacity in the accent colour. Exactly one rail is active. |
| **Inactive icons** | Dimmed to ~70 % opacity; hover surfaces the rail name in a tooltip and lifts the icon to full opacity. |
| **Group separators** | Thin dividers between the declared `Group` buckets (`home`, `work`, `scenarios`, `quality`, `hardening`, `admin`, `help`), so related rails cluster visually. |
| **Bottom cluster** | The Settings gear + theme toggle pin to the bottom of the strip, separated from the rail icons above. |

## The sidebar splitter

A vertical splitter runs down the right edge of the strip, between it and the
[sidebar](sidebar.md). Drag it to widen or narrow the sidebar. The splitter uses
**hover-intent** (a ~250 ms dwell before it highlights) so a cursor passing over
it on the way elsewhere doesn't flash the grab affordance or trigger an
accidental resize.

## The per-rail sub-tab strip

Some rails carry more than one view. Rather than adding more icons to the strip,
those rails render a **sub-tab strip** at the top of their main pane. The order
is fixed per rail so muscle-memory holds:

- **Intercept** → `Captured | Live overrides | Mock servers | Settings`
- **Workspaces** → the detail pane dispatches into Collections / Environments /
  Recordings / Sources / Settings sub-views.

The sub-tab strip is a *within-rail* navigation surface; it never changes the
active rail icon on the strip.

## Cross-rail drop targets

Rail icons double as **drop targets** for cross-rail hand-offs. Dragging a
Discover method (`application/x-bowire-method`) or a recording
(`application/x-bowire-recording`) onto a rail icon that accepts it routes the
payload into that rail — e.g. drop a method onto the Benchmarks icon to seed a
load spec, or a recording onto the Intercept icon to boot a mock server. An icon
highlights while a compatible payload hovers it; incompatible payloads show no
drop affordance. See the [hand-off primitives](../architecture/rail-pipelines.md#hand-off-primitives).

## See also

- [Rail strip (feature)](../features/rail-strip.md) — loading, enabling, ordering, deep-linking
- [Rail pipelines & hand-offs](../architecture/rail-pipelines.md) — per-rail data flow + transitions
- [Sidebar](sidebar.md) — the per-rail list panel to the strip's right
- [Topbar](topbar.md) · [Action bar](action-bar.md)

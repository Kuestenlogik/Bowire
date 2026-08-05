---
title: Side-by-side service compare
summary: 'Pick two versions or deployments of a service and diff them — schema (added / removed / signature-changed methods) and, method by method, the live responses field-by-field. Export the result as a markdown report for a PR comment.'
---

# Side-by-side service compare

When an API is versioned (`v1`, `v2`) or a server is mid-migration, the question is always the same: *does the new one still behave like the old one?* Before [#182](https://github.com/Kuestenlogik/Bowire/issues/182) the only answer was to invoke each side separately and eyeball the responses. The **Compare** surface does it for you.

## Opening it

In the **Discover** rail, a **Compare** button sits in the toolbar whenever there is something to set against something else — at least two services, or at least two discovery URLs. Two URLs matter on their own because discovery de-dupes services by name: the *same* `OrderService` at two deployments collapses to a single row in the tree, so the button stays available to compare it against its other deployment.

Clicking it opens a full-pane compare surface. Pick a **source** (a discovery URL, or the embedded host) and then a **service** on each side — *Baseline (A)* on the left, *Target (B)* on the right. Each side is discovered independently, so two deployments of the same-named service are both reachable even though the tree only shows one.

## What it diffs

### Schema

Methods align by name, and version markers are matched — `GetUser` pairs with `GetUser_v2`, `GET /v1/users` with `GET /v2/users` — so a pure version bump is not mistaken for a change. The aligned methods split into:

| Marker | Meaning |
|---|---|
| `+` (green) | Added — present on the target, not the baseline. |
| `−` (red) | Removed — present on the baseline, gone on the target. |
| `~` (yellow) | Signature changed — same method, but the route, invocation type, request shape, response shape, or `deprecated` flag moved. The row names which facet. |
| `=` | Unchanged (hidden by default; a *show unchanged* toggle reveals them). |

The same AST-level diff powers Schema Watch ([#185](https://github.com/Kuestenlogik/Bowire/issues/185)) — prose-only edits (a changed summary) are not counted as a breaking change.

### Responses

For any aligned **unary** method, **Diff response** invokes it on both sides and compares the two response bodies **field by field, type-aware** — not a line diff:

- `$.total: type number → string` — a field's type moved.
- `$.items.0.sku: added` / `removed` — a field appeared or vanished.
- `$.status: "ok" → "shipped"` — a leaf value changed.

Non-JSON responses fall back to a line diff. **Invoke all & diff responses** runs every aligned unary pair in sequence. (Streaming and duplex methods are schema-compared but not invoke-diffed — there is no single response body to set against another.)

Responses are sent with an empty `{}` body in this first version; for a method that needs a populated request, open it in the normal request builder to shape the call, then compare.

## Export

**Export markdown** downloads a report — the schema summary, the added / removed / signature-changed method lists, and each response field diff — ready to paste into a pull-request comment. This is the same content the v2.5 PR bot will post automatically.

## Notes

- The compare surface is ephemeral: it holds no secrets on disk, and closing it (or switching rails, or opening a method) discards its state. Re-open it to start fresh.
- Both sides discover live, so the comparison always reflects the servers as they are right now.

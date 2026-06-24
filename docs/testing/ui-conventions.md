# Bowire UI conventions

> Reference for label / icon / button style consistency across the workbench. Cited by tickets when a fix requires staying in pattern.

## Button styles

### Primary action button (large, accent-colored)

- Verb-phrase only, **no `+` prefix**.
- Ellipsis `…` (Unicode U+2026) if the click opens a further dialog / multi-step UI; no ellipsis on pure navigation or direct actions.
- Examples: `New workspace…`, `Save now`, `Execute`, `Duplicate…`.

### Secondary action button

- Same label rules as primary (no `+`, ellipsis only on dialog-openers).
- Visually less prominent (lighter border, no accent fill).
- Examples: `Manage workspaces`, `Cancel`.

### Inline list / menu item

- Allowed: leading icon (SVG glyph or text glyph like `+`) followed by the label.
- Same ellipsis rule.
- Examples (dropdown menu): `+ New workspace…`, `▶ Run`, `🗑 Delete`.

### Icon-only toolbar button

- SVG glyph only, no text label.
- `title` + `aria-label` carry the description for screen readers / tooltips.
- Examples: sidebar toolbar `+` (new), `⋮` (overflow), `🔍` (search).

## Labels — same destination, one name

When multiple surfaces lead to the same place, they MUST share the label across every surface. Pick the most operator-centric phrasing (what they'll DO there, not just what they'll see) and stick to it.

| Destination | Canonical label |
|---|---|
| Workspaces overview list | **Manage workspaces** |
| Create-workspace dialog | **New workspace…** |
| Discover rail | **Discover** (rail label) |
| Settings dialog | **Settings** (cog icon) |

Bad: `Show all workspaces` + `All workspaces` + `Manage workspaces` for the same destination.
Good: `Manage workspaces` everywhere.

## Icons — vertical alignment

Icon spans inside menu/list rows MUST use `display: inline-flex; align-items: center; justify-content: center` plus a fixed width/height so the icon sits at the row's vertical centre regardless of whether it's a text glyph (`+`, `▶`) or an SVG.

```css
.bowire-some-row-icon {
    width: 14px;
    height: 14px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
}
.bowire-some-row-icon svg { width: 14px; height: 14px; display: block; }
```

## Empty states

Every empty surface MUST surface ONE clear next step:

- A primary action button (e.g. `New workspace…` on the workspaces overview when empty).
- Or a routing CTA pointing at a surface that has the next step (e.g. `Manage workspaces` on the welcome card).
- Never just an instructional text hint that asks the operator to do something the current surface doesn't expose (e.g. 'Pick a method in the sidebar' when no sidebar method exists — see #272).

## Hover-revealed row tools

Secondary row actions (rename / duplicate / delete) hide until row-hover via `display: none`. State markers (active ✓) get a reserved-width slot via `visibility: hidden` so the row's column layout doesn't shift on hover.

Memory reference: `feedback_hover_reveal_row_tools.md`.

## Quick-access vs. master surfaces

| Surface | Role | What it carries |
|---|---|---|
| **Topbar dropdown / chip** | Quick-access | Switch (list of items) + a single 'New …' shortcut + a 'Manage …' link |
| **Sidebar tree / rail** | Master (per #276) | Per-row tools (hover-revealed + right-click context menu), toolbar with `+ New` + search + sort |
| **Overview / list page** | Wide alternative master | Full table view with bulk select / multi-row tools; reached via 'Manage …' from quick-access surfaces |

The same management actions (rename / duplicate / save-as-template / delete) must be available on BOTH the sidebar and the overview surfaces. The topbar dropdown stays minimal.

## Ellipsis convention

- Unicode `…` (U+2026), **never** three dots `...`.
- **No space** before the `…`.
- Used **only** when the click opens a further UI step (dialog, picker, multi-step). Pure direct actions and pure navigation skip the ellipsis.

Examples:

| Label | Ellipsis? | Reason |
|---|---|---|
| `New workspace…` | yes | opens create-workspace dialog |
| `Manage workspaces` | no | pure navigation to overview |
| `Save now` | no | direct action, no further UI |
| `Duplicate…` | yes | opens prompt for the duplicate's name |
| `Settings` | no | nav to settings dialog (debatable but established in Bowire) |

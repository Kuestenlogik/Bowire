---
title: Topbar
summary: "The topbar runs across the top of the workbench: brand, command palette, and the right-side cluster of session controls."
---

# Topbar

The topbar runs across the top of the workbench above the sidebar, request pane, and response pane. It hosts the brand, the command palette / global search, and the right-side cluster of session controls.

## Layout

```
┌──────────────────────────────────────────────────────────────────────────┐
│  🟢 Bowire   |   ⌕ Search methods, services, hints…   |   ●  api.…  │  │
│                                                          ⓘ env  ⟲ watch │
│                                                          ☼ theme  ✨ AI  │
│                                                          ?  about  ⚙ set │
└──────────────────────────────────────────────────────────────────────────┘
```

### Left — brand

- Small logo (matches the favicon) plus the **Bowire** wordmark.
- In embedded mode, the wordmark is replaced by `options.Title` from the host configuration.
- In locked mode (`--lock-server-url`), a subtitle line shows which URL the workbench is pinned to.

### Center — command palette

- Type-ahead search box that filters the sidebar's service tree **and** opens a suggestions dropdown for quick navigation.
- Live-matches methods, services, recent calls, hints (with the `hint` prefix), and AI queries (with the `@ai` prefix).
- Apply a substring as a name filter chip; press Enter to navigate to the first match.
- Keyboard shortcut: `Ctrl/Cmd+K` focuses the palette from anywhere.

### Right — session controls

The right cluster is the at-a-glance status row plus the per-session toggles.

| Control | Purpose |
|---|---|
| **Connection pill** | Aggregate state of every configured discovery URL — green when all are connected, amber when partial / connecting, red when any failed. Hover for a per-URL breakdown with service counts + retry. ([#93](https://github.com/Kuestenlogik/Bowire/issues/93)) |
| **Environment selector** | Switch active environment; click to manage variables. |
| **Schema watch** | Toggle the background re-discovery loop that polls the configured URLs every 15 s. Useful when developing against a service whose proto / OpenAPI / schema is changing under you. |
| **Theme toggle** | Cycle auto → dark → light → auto. Keyboard shortcut: `t`. |
| **AI drawer** | Open / close the right-side AI assistant. Badge shows live hint-engine count. Keyboard shortcut: `Ctrl/Cmd+Shift+A`. |
| **About** | Standalone dialog with version, open-source notices, and Küstenlogik credit. |
| **Settings** | Settings dialog (General / Shortcuts / Data / AI / Plugins). |

## Connection pill — at-a-glance health

The pill collapses every configured discovery URL into a single dot + summary:

| Aggregate state | Dot color | Summary text |
|---|---|---|
| Every URL connected, single URL | green | the URL (truncated) |
| Every URL connected, multi-URL | green | "All N connected" |
| At least one connecting | amber, pulsing | "X / N connecting…" |
| Mixed — some connected, others idle | amber | "X / N connected" |
| At least one failed | red | "X / N failed" |
| No URLs configured | grey | "Pick a URL" |

Hover the pill to open a popover that lists every URL with:

- Status dot + status word.
- The URL, middle-truncated for readability; the full URL is in the row's `title` attribute.
- Service + method counts (only when connected, so the operator sees the real surface they get from this URL).
- The discovery error message, when failed.

Embedded mode hides the pill — the host owns the URL and there's no operator-facing knob to turn.

## Behavior in different modes

- **Standalone (`bowire` CLI without `--url`)** — full topbar including the command palette, brand, and all right-side controls.
- **Standalone locked (`bowire --url …`)** — same layout, but the connection pill shows the locked URL and the editing affordance in the popover is hidden.
- **Embedded (`app.MapBowire(...)` inside the host)** — connection pill hidden (host-managed); other controls unchanged.

## See also

- [Sidebar](sidebar.md) — service list, filter strip, source selector.
- [Request Pane](request-pane.md) — body editor, metadata, schema view.
- [Response Pane](response-pane.md) — response viewer, history, code generation.
- [Action Bar](action-bar.md) — execute button, repeat, status indicators.
- [Keyboard Shortcuts](../features/keyboard-shortcuts.md) — every chord the workbench listens for.

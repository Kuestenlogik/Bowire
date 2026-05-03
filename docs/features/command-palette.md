---
summary: 'Press <kbd>/</kbd> from anywhere in Bowire to open the command palette — a fuzzy-search launcher for methods, environments, and protocol filters.'
---

# Command palette

Press <kbd>/</kbd> from anywhere in Bowire to open the command palette &mdash; a fuzzy-search launcher for methods, environments, and protocol filters. It is the primary way to navigate the UI without a mouse.

## Opening and closing

| Shortcut | Action |
|---|---|
| <kbd>/</kbd> | Open the palette (works outside form inputs) |
| <kbd>Esc</kbd> | Close the palette |
| <kbd>Ctrl</kbd>+<kbd>K</kbd> | Open from anywhere, including inside input fields |

When the palette is open, a translucent overlay dims the rest of the UI and focus moves to the query input.

## What's searched

The palette searches across four indexed kinds in parallel:

- **Methods** &mdash; every discovered service method across every loaded protocol. Matches the method name, the service name, and any summary / description from the schema.
- **Environments** &mdash; jump to an environment to switch the active variable set.
- **Protocols** &mdash; filter the sidebar to a single protocol (e.g. "only gRPC").
- **Recent** &mdash; if the query is empty, the palette shows the last ten methods you invoked.

Matching is fuzzy &mdash; typing `todo get` matches `GetTodos`, `TodoService.Get`, `GetById` (token-based substring, order-insensitive).

## Keyboard navigation

| Shortcut | Action |
|---|---|
| <kbd>↑</kbd> / <kbd>↓</kbd> | Move through the results |
| <kbd>Enter</kbd> | Activate the highlighted result (jump to method, switch environment, filter protocol) |
| <kbd>Tab</kbd> | Cycle result category (Methods &rarr; Environments &rarr; Protocols &rarr; Recent) |

## Beyond the palette

Palette-independent shortcuts for actions that don't need a selection:

| Shortcut | Action |
|---|---|
| <kbd>j</kbd> / <kbd>k</kbd> | Hop between methods in the sidebar (Vim-style) |
| <kbd>f</kbd> | Toggle Form / JSON request body |
| <kbd>r</kbd> | Repeat the last request |
| <kbd>t</kbd> | Cycle theme light &rarr; dark &rarr; auto |
| <kbd>Ctrl</kbd>+<kbd>Enter</kbd> | Execute the current request |

See [Keyboard Shortcuts](keyboard-shortcuts.md) for the full list, including the mobile / accessibility bindings.

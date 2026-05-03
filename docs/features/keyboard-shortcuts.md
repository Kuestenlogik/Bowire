---
summary: 'Bowire supports keyboard shortcuts for fast navigation and invocation without reaching for the mouse.'
---

# Keyboard Shortcuts

Bowire supports keyboard shortcuts for fast navigation and invocation without reaching for the mouse.

## Available Shortcuts

| Shortcut | Action |
|----------|--------|
| `?` | Show keyboard shortcuts help |
| `/` | Focus the search/filter input |
| `t` | Toggle between dark and light theme |
| `f` | Toggle the favorites filter |
| `r` | Repeat the last request |
| `Ctrl+Enter` | Execute the current request |

## Search and Filter

Press `/` to jump to the search input in the sidebar. Start typing to filter services and methods by name. The filter works across all protocols -- gRPC, SignalR, SSE, and any installed plugins.

Press `Escape` to clear the filter and return focus to the main content area.

## Quick Execution

Press `Ctrl+Enter` from anywhere in the request editor to execute the current request immediately. This works for both form and JSON input modes.

Press `r` to replay the last request with the same parameters. This is useful when iterating on a service that you are actively developing.

## Theme Toggle

Press `t` to switch between dark and light themes. The preference is saved in localStorage and persists across sessions.

See also: [UI Guide](../ui-guide/index.md)

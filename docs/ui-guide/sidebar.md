---
summary: 'The sidebar is the main navigation panel on the left side of the Bowire UI.'
---

# Sidebar

The sidebar is the main navigation panel on the left side of the Bowire UI. It lists all discovered services grouped by protocol.

## Service List

Services are displayed as expandable tree nodes. Each service shows:

- **Protocol badge** -- a colored icon indicating the protocol (gRPC, SignalR, SSE)
- **Service name** -- the fully qualified name (e.g., `weather.WeatherService`)
- **Method count** -- the number of methods in the service

Click a service to expand it and see its methods. Each method shows:

- **Method name** -- the callable method name
- **Call type badge** -- Unary, Server Streaming, Client Streaming, or Duplex
- **Star icon** -- click to add/remove from favorites

## Search and Filter

Press `/` or click the search input to filter services and methods by name. The filter is case-insensitive and matches against both service names and method names across all protocols.

Press `Escape` to clear the filter.

## Protocol Tabs

When multiple protocols are registered, the sidebar shows protocol tabs at the top. Click a tab to filter the service list to a single protocol, or click "All" to show services from all protocols.

The `/bowire/api/protocols` endpoint returns the list of registered protocol plugins.

## Favorites

Favorited methods appear in a dedicated section at the top of the sidebar. Click the star icon next to any method to add or remove it. Favorites are stored in localStorage and persist across sessions.

Press `f` to toggle the favorites-only filter, showing only starred methods.

## Collapsible

On mobile or narrow viewports, the sidebar collapses behind a hamburger menu icon. Tap the icon to show/hide the sidebar.

See also: [Favorites & History](../features/favorites-history.md), [Keyboard Shortcuts](../features/keyboard-shortcuts.md)

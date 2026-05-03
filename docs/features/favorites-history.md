---
summary: 'Bowire tracks your request history and lets you star frequently-used methods for quick access.'
---

# Favorites & History

Bowire tracks your request history and lets you star frequently-used methods for quick access.

## Favorites

Click the star icon next to any method in the sidebar to add it to your favorites. Favorited methods appear at the top of the sidebar for quick access, regardless of which protocol they belong to.

Favorites are persisted in the browser's localStorage, so they survive page reloads and browser restarts. They are scoped to the current Bowire instance URL.

## Request History

Every successful request is recorded in the history panel. Each history entry includes:

- **Method name** -- which service and method was called
- **Request body** -- the JSON that was sent
- **Timestamp** -- when the request was made
- **Status** -- the response status code

Click a history entry to replay the request with the same body and metadata. You can edit the replayed request before sending.

## Data Storage

Both favorites and history are stored in the browser's localStorage under keys prefixed with `bowire-`. No data is sent to the server.

To clear history or favorites, use the browser's developer tools to remove the relevant localStorage entries, or use the clear buttons in the UI.

See also: [UI Guide -- Sidebar](../ui-guide/sidebar.md)

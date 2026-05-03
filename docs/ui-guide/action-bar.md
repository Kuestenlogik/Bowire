---
summary: 'The action bar sits between the request and response panes.'
---

# Action Bar

The action bar sits between the request and response panes. It contains the primary controls for executing and managing requests.

## Execute Button

The main **Send** button executes the current request. Its behavior depends on the call type:

| Call Type | Button Label | Behavior |
|-----------|-------------|----------|
| Unary | Send | Sends one request, displays one response |
| Server Streaming | Send | Sends one request, streams responses |
| Client Streaming | Send All | Sends all queued messages, displays one response |
| Duplex | Open Channel | Opens an interactive channel |

The button is disabled while a request is in flight. For streaming calls, it changes to **Stop** to cancel the active stream.

Keyboard shortcut: `Ctrl+Enter`

## Repeat Button

Click **Repeat** or press `r` to re-execute the last request with the same method, body, and metadata. This is useful when iterating on a service during development.

## Status Indicator

The action bar shows the current connection status:

- **Ready** -- waiting for a request
- **Sending** -- request in flight
- **Streaming** -- receiving streaming responses
- **Error** -- last request failed
- **Channel Open** -- interactive channel is active

## Server URL

The server URL is displayed in the action bar. By default, it connects to the same server that serves the Bowire UI. Click the URL to edit it, or configure it at startup:

```csharp
app.MapBowire(options =>
{
    options.ServerUrl = "https://grpc.example.com:443";
    options.LockServerUrl = true; // Prevent editing in the UI
});
```

See also: [Keyboard Shortcuts](../features/keyboard-shortcuts.md), [Streaming](../features/streaming.md)

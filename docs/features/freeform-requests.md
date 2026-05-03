---
summary: 'The freeform request builder lets you craft and execute API requests manually, without relying on service discovery.'
---

# Freeform Request Builder

The freeform request builder lets you craft and execute API requests manually, without relying on service discovery. This is useful for ad-hoc testing, working with endpoints that don't expose reflection or OpenAPI metadata, or quickly firing a request before setting up a full connection.

## Creating a freeform request

Click the **+** button in the sidebar header. A dropdown lists the available protocols -- select one to open the freeform request builder in the main pane.

If protocol plugins are loaded, all registered protocols appear in the dropdown. If no plugins are loaded yet, a default set is shown: gRPC, REST, GraphQL, SignalR, MQTT, WebSocket.

## The builder form

The freeform builder provides a simple form with the following fields:

| Field | Description |
|-------|-------------|
| **Protocol** | Dropdown to select the protocol (gRPC, REST, GraphQL, etc.) |
| **Method Type** | Dropdown to select the call pattern: Unary, ServerStreaming, ClientStreaming, or Duplex |
| **Server URL** | The target server address |
| **Service** | The service name (e.g. `weather.WeatherService`, `/api/users`) |
| **Method** | The method name (e.g. `GetCurrentWeather`, `POST`) |
| **Body** | A JSON text editor for the request payload |

The protocol and method type dropdowns update the `freeformRequest` object in memory. Changes are not persisted until you save to a collection.

## No discovery needed

The key difference between freeform requests and the normal sidebar workflow is that freeform requests bypass discovery entirely. You do not need to:

- Connect to a server first
- Wait for reflection or schema introspection
- Have a running service that advertises its methods

Just type in the service, method, and body, then execute.

## Inline JSON validation

The body editor includes real-time JSON validation. As you type, a status indicator below the editor shows whether the current content is valid JSON. This catches syntax errors (missing commas, unmatched braces) before you send the request.

The validator runs on every keystroke without blocking the UI.

## Executing the request

Click **Execute** (or press `Ctrl+Enter`) to send the request. Bowire routes the freeform request through the same `/api/invoke` endpoint used by discovered methods, so all protocol-specific handling, variable substitution, and response rendering work identically.

The response appears directly below the builder form with the same syntax-highlighted JSON viewer used in the standard response pane.

## Canceling

Click **Cancel** in the builder header to discard the freeform request and return to the previous view (either the selected method or the empty-state landing page).

## Save to Collection

Click **Save to Collection** to add the freeform request to a collection. The request is stored with its protocol, service, method, method type, body, and server URL.

If you have existing collections, the request is added to the first collection. If you have no collections yet, a new one is created automatically.

Saving to a collection makes the request permanent and replayable via the Collection Runner.

## Example: ad-hoc REST call

```
Protocol:    REST
Method Type: Unary
Server URL:  https://api.example.com
Service:     /api/users
Method:      POST
Body:
{
  "name": "Alice",
  "email": "alice@example.com"
}
```

Click Execute. The response renders below the form. Click Save to Collection to keep it for later.

## Example: gRPC without reflection

```
Protocol:    gRPC
Method Type: Unary
Server URL:  https://grpc.example.com:443
Service:     weather.WeatherService
Method:      GetCurrentWeather
Body:
{
  "city": "Berlin"
}
```

This calls the gRPC method directly. Even if the server does not expose gRPC Server Reflection, the request is sent as long as the service and method names are correct.

## Tips

- Use freeform requests for **quick one-off calls** when you know the endpoint but don't want to wait for discovery.
- The freeform builder respects the current **environment** -- `${baseUrl}` and other placeholders are substituted before execution.
- After executing, save the request to a collection so you don't have to type it again.
- Combine with [CLI Mode](cli-mode.md) for scripted ad-hoc calls from the terminal.

See also: [Collections](collections.md), [Form & JSON Input](form-json-input.md), [Auto-Discovery](auto-discovery.md)

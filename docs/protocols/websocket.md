---
title: WebSocket
summary: 'The WebSocket plugin opens an interactive bidirectional channel against any WebSocket endpoint and lets the user send raw text or binary frames and watch responses arrive in real t'
---

# WebSocket Protocol

The WebSocket plugin opens an interactive bidirectional channel against any WebSocket endpoint and lets the user send raw text or binary frames and watch responses arrive in real time. There is no protocol-specific framing or schema — WebSocket is a transport, and the plugin treats it as one.

**Package:** `Kuestenlogik.Bowire.Protocol.WebSocket`

## Setup

### Standalone

```bash
bowire --url ws://localhost:5009/ws/echo
```

Point Bowire at the WebSocket endpoint URL (`ws://` or `wss://`). The plugin synthesizes a single ad-hoc service named `WebSocket` with one method whose name is the path of the URL, and the channel opens against that URL when the user clicks **Connect**.

### Embedded

Two ways to register an endpoint so it shows up in the sidebar of an embedded Bowire:

1. Mark the endpoint with `[WebSocketEndpoint]` metadata:

   ```csharp
   app.Map("/ws/chat", async (HttpContext ctx) =>
   {
       using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
       // ...
   })
   .WithMetadata(new WebSocketEndpointAttribute("Chat", "Group chat WebSocket."));
   ```

2. Or register the endpoint declaratively in your DI setup before `MapBowire()`:

   ```csharp
   services.AddBowireWebSocketEndpoints(registry =>
   {
       registry.Add(new WebSocketEndpointInfo("/ws/chat", "Chat", "Group chat WebSocket."));
   });
   ```

   (Pre-v1.7 callers used the static `BowireWebSocketProtocol.RegisterEndpoint(...)`; v1.7 swaps it for the DI-resolved `IWebSocketEndpointRegistry` so each ASP.NET host carries its own list and the registration survives an in-process restart without leaking endpoints across hosts.)

The embedded discovery scans `EndpointDataSource` for `WebSocketEndpointAttribute`-tagged routes and merges them with anything in the registered list. `http://` and `https://` base URLs are auto-rewritten to `ws://` / `wss://` so the discovered methods open against the right scheme without any extra config.

## Channel model

Every WebSocket "method" is a duplex channel:

- The user opens the channel from the form (or with `bowire channel open`)
- Outgoing messages from the form become text frames by default
- Incoming frames are pushed back as JSON envelopes:

  ```json
  { "type": "text", "text": "hello", "bytes": 5 }
  { "type": "binary", "bytes": 12, "base64": "aGVsbG8gd29ybGQh" }
  { "type": "close", "status": 1000, "description": "bye" }
  ```

- Closing the channel sends a normal-closure frame to the server

### Sending raw text

The simplest case — type a string in the form's `data` field, click **Send**, and the plugin emits a single text frame.

The form wraps it as `{ "data": "hello bowire" }`; the plugin recognises that shape and sends `hello bowire` as a UTF-8 text frame.

### Sending raw text or binary explicitly

If you want full control over the frame type, use the JSON editor and submit one of:

```json
{ "type": "text", "text": "raw text frame" }
{ "type": "binary", "base64": "SGVsbG8gV2ViU29ja2V0IQ==" }
```

Binary frames are decoded server-side from the base64 payload and sent as a single binary frame.

### Receiving binary frames

Binary frames from the server come back as JSON envelopes with `type: "binary"`, the byte count, and a base64-encoded payload. Decode them client-side however you want — the response viewer shows them as JSON, the console log shows the byte count.

## Authentication

Custom request headers (auth, API keys, ...) are forwarded via `ClientWebSocket.Options.SetRequestHeader` before the handshake. The existing per-environment auth-helper pipeline (Bearer / Basic / API Key / JWT / OAuth) works without any plugin-specific code: every header it adds rides along with the WebSocket upgrade request.

## Sub-protocol negotiation

WebSocket sub-protocols (`Sec-WebSocket-Protocol`) are how clients and servers agree on a higher-level message format on top of raw WebSocket — `graphql-transport-ws`, `mqtt`, `wamp.2.json`, etc. Bowire forwards them in two ways:

**Via metadata header** — set the magic key `X-Bowire-WebSocket-Subprotocol` on the request. Comma-separated values are split into individual sub-protocols. The key is consumed before the rest of the metadata is forwarded as HTTP headers, so the marker never reaches the wire. Example:

```
X-Bowire-WebSocket-Subprotocol: graphql-transport-ws
```

**Via the cross-plugin interface** — the GraphQL plugin's subscription path uses `IInlineWebSocketChannel.OpenAsync(url, subProtocols, headers, ct)` to open a channel with `graphql-transport-ws` without taking a compile-time dependency on the WebSocket plugin. Other plugins can do the same — implementers should pass the requested sub-protocols to `ClientWebSocket.Options.AddSubProtocol(...)` before the handshake.

The negotiated sub-protocol from the server's response is available on the underlying `ClientWebSocket.SubProtocol` if you need it programmatically — Bowire itself doesn't currently surface it in the channel UI, but the connection only succeeds when the server agrees on one of the offered values.

## Limitations

- **No frame fragmentation.** Bowire sends every outgoing message as a single complete frame. Long messages are not split into continuation frames.
- **The negotiated sub-protocol isn't surfaced in the channel UI.** It's still bound to the connection — server-side validation works correctly — but you don't see which value the server picked from your offered list.

## Sample

[`samples/Kuestenlogik.Bowire.Sample.WebSocket`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.WebSocket) is a combined server + embedded-workbench sample. It runs a WebSocket echo endpoint at `/ws` on port 5185 — every text frame is echoed back prefixed with `echo: ` — and mounts the embedded Bowire workbench at `/bowire`, with the endpoint pre-seeded into the Sources rail, so the one project demonstrates both ways Bowire meets a WebSocket service.

Run it:

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.WebSocket
```

- **Embedded** — open <http://localhost:5185/bowire>; the `/ws` echo endpoint is already in the Sources rail.
- **Separate target** — point the standalone CLI at the running server:

  ```bash
  bowire --url websocket@ws://localhost:5185/ws
  ```

See also: [Quick Start](../setup/index.md), [Plugin System](../features/plugin-system.md)

---
summary: 'Bowire supports all streaming patterns for gRPC and SignalR, with real-time response display powered by Server-Sent Events (SSE).'
---

# Streaming

Bowire supports all streaming patterns for gRPC and SignalR, with real-time response display powered by Server-Sent Events (SSE).

## Call Types

| Type | Client Sends | Server Sends | Use Case |
|------|-------------|-------------|----------|
| **Unary** | 1 message | 1 message | Request/response (most common) |
| **Server Streaming** | 1 message | N messages | Live feeds, progress updates |
| **Client Streaming** | N messages | 1 message | File uploads, batch operations |
| **Duplex (Bidi)** | N messages | N messages | Chat, real-time collaboration |

## Server Streaming

1. Select a server streaming method (marked with a streaming badge)
2. Fill in the request JSON
3. Click **Send**
4. Responses appear in real-time as they arrive
5. Click **Stop** to cancel the stream early

Each incoming message is appended to the response viewer with a timestamp. A streaming indicator shows the connection is active.

## Client Streaming

1. Select a client streaming method
2. Type a JSON message and click **Add** to queue it
3. Repeat to add more messages
4. Click **Send All** to transmit all messages and receive the response

The queued messages are shown in a list above the editor. You can remove individual messages before sending.

## Duplex (Bidirectional)

See [Duplex Channels](duplex-channels.md) for the interactive duplex experience.

## Technical Implementation

Bowire delivers streaming responses to the browser via SSE. The browser establishes an `EventSource` connection to `/bowire/api/invoke/stream`, and the server pushes events as they arrive from the target service.

SSE event types:

| Event | Description |
|-------|-------------|
| `message` | A response message (JSON-encoded) |
| `status` | Final status when the stream completes normally |
| `error` | Error event when the stream fails |

This approach works without WebSockets, keeping the implementation simple and compatible with all reverse proxies.

## Metadata (Headers)

All streaming calls support metadata. Click the **Headers** button in the request panel to add key-value pairs sent as gRPC metadata or SignalR headers with the call.

See also: [Duplex Channels](duplex-channels.md), [Protocols](../protocols/index.md)

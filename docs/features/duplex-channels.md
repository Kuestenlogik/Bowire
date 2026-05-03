---
summary: 'Bowire provides interactive bidirectional channels for duplex and client-streaming methods.'
---

# Duplex Channels

Bowire provides interactive bidirectional channels for duplex and client-streaming methods. A channel keeps the connection open so you can send and receive messages in real time.

## How It Works

When you select a duplex or client-streaming method and click **Open Channel**, Bowire creates an `IBowireChannel` that maintains a persistent connection to the target service:

1. The UI shows a split view: **Send** panel and **Receive** panel
2. Type messages in the Send panel and click **Send** to transmit each one
3. Received messages appear in the Receive panel in real-time
4. A counter shows messages sent and elapsed time
5. Click **Close** to end the channel from the client side

## Channel Status

The channel displays live status information:

- **Sent count** -- number of messages transmitted so far
- **Elapsed time** -- how long the channel has been open
- **Connection state** -- whether the channel is open or closed

## gRPC Duplex Example

For a gRPC chat service:

```protobuf
service ChatService {
  rpc Chat (stream ChatMessage) returns (stream ChatMessage);
}
```

Open a channel, type a message, and send it. Responses from the server appear instantly in the receive panel. Both directions operate independently -- you can send while receiving.

## SignalR Duplex

SignalR hub methods that accept `ChannelReader<T>` and return `IAsyncEnumerable<T>` or `ChannelReader<T>` are treated as duplex. The channel behavior is the same as for gRPC.

## Client Streaming Channels

For client-streaming methods, the channel allows sending multiple messages before closing. The response appears after you click **Close** to signal completion.

## Protocol Support

| Protocol | Duplex | Client Streaming |
|----------|--------|-----------------|
| gRPC | Yes | Yes |
| SignalR | Yes | Yes |
| SSE | No (server-to-client only) | No |

See also: [Streaming](streaming.md), [gRPC Protocol](../protocols/grpc.md)

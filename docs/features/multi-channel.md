---
summary: 'The multi-channel manager lets you keep multiple persistent connections (WebSocket, SignalR, MQTT, duplex gRPC, etc.) open simultaneously and switch between them without losing sta'
---

# Multi-Channel Manager

The multi-channel manager lets you keep multiple persistent connections (WebSocket, SignalR, MQTT, duplex gRPC, etc.) open simultaneously and switch between them without losing state.

## The problem it solves

Without multi-channel support, switching from one duplex method to another would disconnect the first channel. This makes it impossible to monitor two WebSocket streams at once or keep a SignalR hub connected while testing a gRPC streaming method.

The multi-channel manager solves this with a **stash/restore pattern**: when you navigate away from a method that has an open channel, the connection state is stashed. When you navigate back, it is restored -- the connection is still live, messages are still flowing, and the UI picks up right where you left off.

## How it works

Bowire maintains an in-memory store of open channels, keyed by `service::method`. Each entry preserves:

| Field | Description |
|-------|-------------|
| `channelId` | The server-side channel identifier |
| `connected` | Whether the channel is currently connected |
| `sseSource` | The active SSE event source for receiving messages |
| `sentCount` | Number of messages sent on this channel |
| `receivedCount` | Number of messages received on this channel |
| `channelError` | Last error (if any) |
| `streamMessages` | All messages received so far |

### Stash

When you click a different method in the sidebar, `stashCurrentChannel()` saves the current channel's state into the store. The SSE source remains connected -- it is not closed.

### Restore

When you navigate to a method that has a stashed channel, `restoreChannelFor()` loads the saved state back into the active UI variables. The response pane shows the channel's messages, the sent/received counters are accurate, and the connect/disconnect button reflects the real connection state.

If the target method has no stashed channel, all channel state resets to defaults (disconnected, zero counters, empty message list).

## WebSocket example

Here is a typical multi-channel workflow:

1. Navigate to **ChatService/Connect** (a WebSocket duplex method).
2. Click **Connect**. The WebSocket opens, messages start arriving.
3. Send a few messages. The sent/received counters update.
4. Click **NotificationService/Subscribe** in the sidebar to switch methods.
5. The WebSocket channel is stashed -- it stays connected in the background.
6. Connect to the notification stream. Messages arrive on this channel too.
7. Click **ChatService/Connect** again.
8. The chat channel is **restored** -- all previous messages are visible, the counters show the accumulated totals (including messages received while you were on the other method), and the connection is still live.

## Active job indicators

Methods with an active connection or executing request show a **play icon** in the sidebar. This provides at-a-glance visibility into which methods currently have background activity.

The indicator uses the `activeJobs` tracking set, which is updated whenever:

- A channel is opened (added to active jobs)
- A channel is closed or errors out (removed from active jobs)
- A long-running request starts or completes

The sidebar re-renders whenever you switch methods, so indicators stay current.

## Multiple protocols

The stash/restore pattern is protocol-agnostic. You can have channels open across different protocols simultaneously:

- A **WebSocket** connection to a chat server
- A **SignalR** hub connection for real-time notifications
- A **gRPC duplex** stream for bidirectional messaging
- An **MQTT** subscription to a topic

Each channel is independent. Switching between them preserves all state.

## Cleanup

When you explicitly **disconnect** a channel (via the disconnect button), its entry is removed from the store and the SSE source is closed. This frees server-side resources.

Channels are also cleaned up when:

- The browser tab is closed or refreshed (SSE sources close automatically)
- The server drops the connection (the channel error state is preserved so you see the error when you return)

## Method state preservation

In addition to channel state, Bowire also preserves the **form/JSON editor state** for each method. When you switch away from a method, the current request body, form field values, and input mode (Form vs. JSON) are saved. Switching back restores them, so you never lose work when navigating between methods.

This works independently of the channel stash -- even methods without open channels get their editor state preserved.

## Memory considerations

All stashed channels keep their message history in memory. If a channel receives a high volume of messages while stashed, memory usage grows. For long-running monitoring scenarios, consider periodically disconnecting and reconnecting to clear the message buffer.

## How it differs from the Recorder

The Recorder captures calls into a sequence for replay. The multi-channel manager does not capture anything -- it keeps live connections open. Use the Recorder when you need to save a reproducible scenario. Use multi-channel when you need to work with multiple live connections at the same time.

## Tips

- Use multi-channel to **compare responses** from two endpoints side by side -- connect both, then switch back and forth to see their outputs.
- The sidebar's active job indicator tells you at a glance which methods have live connections, even when you are looking at a different method.
- Multi-channel state is **in-memory only** -- it does not survive page reloads. After a refresh, all channels are disconnected and must be re-established.

See also: [Duplex Channels](duplex-channels.md), [Streaming](streaming.md)

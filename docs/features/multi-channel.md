---
title: Multi-channel
summary: 'The multi-channel manager lets you keep multiple persistent connections (WebSocket, SignalR, MQTT, duplex gRPC, etc.) open simultaneously and switch between them without losing sta'
---

# Multi-Channel Manager

The multi-channel manager lets you keep multiple persistent connections (WebSocket, SignalR, MQTT, duplex gRPC, etc.) open simultaneously and switch between them without losing state.

## The problem it solves

Without multi-channel support, switching from one duplex method to another would disconnect the first channel. This makes it impossible to monitor two WebSocket streams at once or keep a SignalR hub connected while testing a gRPC streaming method.

Bowire solves this by making the **tab** the home of a connection: every request tab carries its own channel state, and a tab that has something live is never reused for another method. Two tabs, two channels, both connected -- switching tabs changes which one is on screen, nothing is swapped in or out.

## How it works

Each tab's state holds, alongside its request and response, the channel it opened:

| Field | Description |
|-------|-------------|
| `duplexChannelId` | The server-side channel identifier |
| `duplexConnected` | Whether the channel is currently connected |
| `duplexSseSource` | The active SSE event source for receiving messages |
| `sentCount` | Number of messages sent on this channel |
| `receivedCount` | Number of messages received on this channel |
| `channelError` | Last error (if any) |
| `streamMessages` | All messages received so far |
| `sentMessages` | Messages sent, with their offset from `channelStartMs` |

### Opening another method while a channel is live

Clicking a method in the sidebar normally reuses the active tab (browser-style navigation). When the active tab has an open channel -- or a running stream, or a request still in flight -- it is **not** reused: the new method opens in a tab beside it. The channel stays connected in its own tab, and its messages keep arriving there while you work in the other one.

### Switching tabs

Switching to a tab shows that tab's channel exactly as it is: the response pane lists its messages, the sent/received counters are its own, and the connect/disconnect button reflects the real connection state. Nothing is restored, because nothing was taken away.

### Closing a tab

The tab is the connection's only home. Closing a tab with a live channel closes the channel (and a running stream, or an in-flight request); the same happens when a tab with nothing live is given to another method.

## WebSocket example

Here is a typical multi-channel workflow:

1. Navigate to **ChatService/Connect** (a WebSocket duplex method).
2. Click **Connect**. The WebSocket opens, messages start arriving.
3. Send a few messages. The sent/received counters update.
4. Click **NotificationService/Subscribe** in the sidebar.
5. Because the chat tab has a live channel, the notification method opens in a **second tab**. The chat channel stays connected in the first.
6. Connect to the notification stream. Messages arrive on this channel too.
7. Click the **ChatService/Connect** tab.
8. All previous messages are visible, the counters show the accumulated totals (including messages received while you were on the other tab), and the connection is still live.

## Active job indicators

Methods with an active connection or executing request show a **play icon** in the sidebar. This provides at-a-glance visibility into which methods currently have background activity.

The indicator uses the `activeJobs` tracking set, which is updated whenever:

- A channel is opened (added to active jobs)
- A channel is closed or errors out (removed from active jobs)
- A long-running request starts or completes

The sidebar re-renders whenever you switch methods, so indicators stay current.

## Multiple protocols

Tab-owned state is protocol-agnostic. You can have channels open across different protocols simultaneously, one per tab:

- A **WebSocket** connection to a chat server
- A **SignalR** hub connection for real-time notifications
- A **gRPC duplex** stream for bidirectional messaging
- An **MQTT** subscription to a topic

Each channel is independent. Switching tabs changes which one you see, not which ones are open.

## Cleanup

When you explicitly **disconnect** a channel (via the disconnect button), or close its tab, the SSE source is closed and the server-side channel is released.

Channels are also cleaned up when:

- The browser tab is closed or refreshed (SSE sources close automatically)
- The server drops the connection (the channel error state is preserved so you see the error when you return)

## Request state

The tab owns its request as well: the body, the form field values, and the input mode (Form vs. JSON) stay with the tab while you work in another one. Reusing a tab for a different method starts that method fresh; to keep what you typed, open the other method in a new tab (Ctrl/Cmd+click, middle-click, or the row's context menu).

## Memory considerations

Every open channel keeps its message history in memory. If a channel receives a high volume of messages while its tab is in the background, memory usage grows. For long-running monitoring scenarios, consider periodically disconnecting and reconnecting to clear the message buffer.

## How it differs from the Recorder

The Recorder captures calls into a sequence for replay. The multi-channel manager does not capture anything -- it keeps live connections open. Use the Recorder when you need to save a reproducible scenario. Use multi-channel when you need to work with multiple live connections at the same time.

## Tips

- Use multi-channel to **compare responses** from two endpoints -- connect both in their own tabs, then switch back and forth to see their outputs.
- The sidebar's active job indicator tells you at a glance which methods have live connections, even when you are looking at a different method.
- Multi-channel state is **in-memory only** -- it does not survive page reloads. After a refresh, all channels are disconnected and must be re-established.

See also: [Duplex Channels](duplex-channels.md), [Streaming](streaming.md)

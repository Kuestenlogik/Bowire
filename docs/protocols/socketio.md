---
summary: 'Bowire connects to Socket.IO 4.x servers, discovers events during a short listen window, and provides emit (unary) and listen (streaming) methods.'
---

# Socket.IO Protocol

Bowire connects to Socket.IO 4.x servers, discovers events during a short listen window, and provides emit (unary) and listen (streaming) methods.

## Setup

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.SocketIo
```

### Standalone

```bash
bowire --url http://localhost:3000
```

## Discovery

On connect, Bowire listens for 2 seconds and collects every event name received. These become specific listen methods. Two generic methods are always available:

- **emit** (Unary) -- send an event with a JSON payload
- **listen** (ServerStreaming) -- subscribe to all or specific events

## Emit Input

```json
{
  "event": "message",
  "data": { "text": "hello" }
}
```

## Listen Output

Each received event is streamed as:

```json
{
  "event": "temperature",
  "data": "{ \"value\": 22.5 }",
  "timestamp": "2026-04-10T12:00:00Z"
}
```

---
title: SignalR
summary: 'The SignalR plugin auto-discovers mapped hubs and their methods via endpoint metadata scanning.'
---

# SignalR Protocol

The SignalR plugin auto-discovers mapped hubs and their methods via endpoint metadata scanning.

**Package:** `Kuestenlogik.Bowire.Protocol.SignalR`

## Setup

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.SignalR
```

```csharp
builder.Services.AddSignalR();

var app = builder.Build();

app.MapHub<ChatHub>("/chathub");
app.MapBowire();                    // Auto-discovers hubs
```

Hubs must be mapped with `MapHub<T>()` before `MapBowire()`. Bowire discovers them by scanning the application's `EndpointDataSource` for endpoints with `HubMetadata`.

## Discovery

The SignalR plugin reflects each hub class to determine:

- **Methods** -- all public methods on the hub
- **Parameter types** -- displayed as proto-like type names (string, int32, etc.)
- **Return types** -- unwrapped from `Task<T>`, `ValueTask<T>`
- **Streaming direction** -- inferred from parameter and return types:
  - `IAsyncEnumerable<T>` or `ChannelReader<T>` return = server streaming
  - `ChannelReader<T>` parameter = client streaming
  - Both = duplex

## Call Types

### Invoke (Unary)

Standard hub method invocation. Send a JSON object with parameter values, receive the return value.

### Server Streaming

Hub methods returning `IAsyncEnumerable<T>` or `ChannelReader<T>` are treated as server streaming. Messages appear in the response viewer as they arrive.

### Duplex

Hub methods that accept `ChannelReader<T>` and return a streaming type support duplex communication via interactive channels.

## Separate targets (`signalr@`)

Method-level discovery needs the embedded endpoint metadata, but a
standalone workbench can still drive a remote hub:

```bash
bowire --url signalr@https://api.example.com/hubs/chat
```

The plugin confirms hub-ness via the `negotiate` handshake and exposes an
ad-hoc **SignalR Hub** service with two generic entry points — SignalR has
no wire-level reflection, so you name the hub method yourself:

- `invoke` -- calls a hub method once and returns its result.
- `stream` -- subscribes to a streaming hub method.

Both take the same payload: `method` (the hub method name) plus `args`,
one JSON value per positional parameter (`42`, `"text"`, `{"x":1}` — bare
words are sent as strings):

```json
{ "method": "Echo", "args": ["hello"] }
```

## Limitations

- **Method lists are embedded-only** -- the ad-hoc `signalr@` surface can call and stream, but listing a remote hub's methods is impossible without the in-process endpoint metadata; consult the target's own docs for method names.
- **Hub methods only** -- broadcast methods invoked via `Clients.All.SendAsync()` are not discoverable because they are not defined on the hub class.

## Example

Given a hub:

```csharp
public class ChatHub : Hub
{
    public Task<string> Echo(string message) => Task.FromResult(message);

    public async IAsyncEnumerable<int> Counter(int count, int delay)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.Delay(delay);
        }
    }
}
```

Bowire discovers two methods:

- `Echo` -- unary, takes a string parameter, returns a string
- `Counter` -- server streaming, takes two int parameters, streams int responses

See also: [Quick Start](../setup/index.md), [Duplex Channels](../features/duplex-channels.md)

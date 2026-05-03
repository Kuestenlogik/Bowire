---
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

## Limitations

- **Embedded mode only** -- SignalR discovery requires access to the application's endpoint metadata, which is only available when Bowire runs inside the same process. Standalone mode does not support SignalR discovery.
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

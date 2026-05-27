---
summary: 'Generic JSON-RPC 2.0 client. Discovery via OpenRPC `rpc.discover`, freeform fallback for servers that do not advertise it. In-tree plugin, no separate install.'
---

# JSON-RPC Protocol

Bowire calls any JSON-RPC 2.0 server over HTTP and discovers its surface via the **OpenRPC** `rpc.discover` reflection method. Servers without OpenRPC still work — the plugin claims the URL and exposes a "Methods" placeholder so the user can invoke any method by name.

## Setup

In-tree plugin — no separate install needed.

### Standalone

```bash
bowire --url http://localhost:5000/rpc
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("http://localhost:5000/rpc");
});
```

## Discovery

`DiscoverAsync` calls `rpc.discover` (the OpenRPC convention). When the server returns an OpenRPC document, each method maps to a Bowire method with its `params` array as the input field list. When the server returns Method-Not-Found (`-32601`), Bowire still claims the URL and shows a placeholder service so freeform invocation works.

## Invocation

The first JSON message in the request becomes the JSON-RPC `params` field. Both shapes are accepted:

- **Positional**: `[1, 2]`
- **Named**: `{"a": 1, "b": 2}`
- **Empty / whitespace**: `params` is omitted entirely

## Error Handling

JSON-RPC application errors surface with `Status="jsonrpc:<code>"` so the UI can tell `-32601 Method not found` apart from transport-level failures. The error body (when the server returns one in `error.data`) is JSON-serialised back into the response field.

## URL Plumbing

- `http://host:port/path` — used verbatim
- `https://host:port/path` — used verbatim
- `jsonrpc@http://...` — URL-prefix hint that pins this plugin; the prefix is stripped before the URL reaches the wire

## Streaming

JSON-RPC 2.0 has no streaming primitive — `InvokeStreamAsync` always returns an empty enumerable. Embedded hosts that wrap JSON-RPC over WebSocket can pair this plugin with the WebSocket plugin for that surface.

## Sample

A runnable sample lives at [`samples/JsonRpc/Math`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/JsonRpc/Math) — `add` / `subtract` / `divide` with full `rpc.discover`.

---
summary: 'Sidecar plugins let any-language protocol clients (Python, Go, Rust, Node) implement IBowireProtocol by running as an external process that speaks JSON-RPC 2.0 over stdin/stdout.'
---

# Sidecar plugins (JSON-RPC over stdio)

Bowire's first-party protocol plugins are .NET assemblies implementing
[`IBowireProtocol`](../../src/Kuestenlogik.Bowire/IBowireProtocol.cs).
That works fine when the best client for a protocol ships as a NuGet
package — gRPC, REST, GraphQL, NATS.Net, DotPulsar all qualify. It
falls apart when the canonical library lives in another ecosystem:
Zenoh (Rust), `paho-mqtt` + the SciPy/ML stack (Python), the Temporal
SDK (Go), `socket.io-client` (Node.js). Porting each library to .NET
isn't viable.

**Sidecar plugins** solve this. A sidecar is an executable in any
language that implements the
[`IBowireProtocol`](../../src/Kuestenlogik.Bowire/IBowireProtocol.cs)
contract by speaking **JSON-RPC 2.0 over its own stdin/stdout**. The
Bowire host spawns the process, talks to it via JSON-RPC, and exposes
the result in the workbench like any other protocol plugin.

The transport choice (JSON-RPC over stdio) matches what the Language
Server Protocol, Debug Adapter Protocol, and Model Context Protocol
all settled on independently. The framing is **NDJSON**: each JSON-RPC
envelope is a single UTF-8 line terminated by `\n` (same as MCP's
stdio transport). No `Content-Length` header, no chunking.

## Layout on disk

Sidecars install into the same per-user plugin tree the .NET plugins
use:

```
~/.bowire/plugins/<package-id>/
  sidecar.json                   # manifest (this file is the marker)
  bin/                           # vendor's choice — could be anything:
    my-sidecar                   # a native binary
    my-sidecar.exe               # …or a Windows binary
    my-sidecar.py                # …or a Python script
    package.json                 # …or a Node.js entry point
```

The `sidecar.json` file at the directory root is what marks the
directory as a sidecar plugin. (It's deliberately **not** named
`plugin.json` — that filename is taken by the NuGet-install metadata
file the .NET plugin path writes, and the two plugin kinds share the
same `~/.bowire/plugins/` tree.) The Bowire host scans every
`sidecar.json` in the plugin tree at startup, registers one
[`SidecarBowireProtocol`](../../src/Kuestenlogik.Bowire/Plugins/Sidecar/SidecarBowireProtocol.cs)
per manifest, and proxies every call through to the process.

## Installing

Ship the sidecar as a `.zip` containing `sidecar.json` at its root
plus the executable and any runtime files. Install it with the same
command the .NET plugins use — the `.zip` extension routes to the
sidecar path:

```bash
bowire plugin install --file ./my-sidecar.zip          # local
bowire plugin install --file https://example.com/x.zip # http(s) URL
```

The archive unpacks into `~/.bowire/plugins/<packageId>/` (packageId
read from the manifest). On Unix the executable bit is restored after
extraction. `bowire plugin list` tags the result `[sidecar: <id>]` to
distinguish it from `[nuget: N files]` .NET plugins.

## Manifest schema

```json
{
  "$schema": "https://bowire.io/schemas/plugin.schema.json",
  "packageId": "Acme.Bowire.Protocol.Zenoh",
  "protocol": {
    "id": "zenoh",
    "name": "Zenoh",
    "iconSvg": "<svg viewBox=\"0 0 24 24\">...</svg>"
  },
  "executable": "bin/bowire-zenoh-sidecar",
  "args": ["--quiet"],
  "envPrefix": "BOWIRE_ZENOH_",
  "shutdownTimeoutMs": 3000
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `packageId` | yes | Reverse-DNS package id, surfaced in `bowire plugin list`. |
| `protocol.id` | yes | The id the workbench tabs against (e.g. `zenoh`); must match what `initialize` returns. |
| `protocol.name` | yes | Display name (e.g. `Zenoh`). |
| `protocol.iconSvg` | no | Inline SVG. Initial-handshake `initialize` response can override. |
| `transport` | no | `"stdio"` (default) or `"http"`. Picks the wire — see below. |
| `executable` | stdio | Path to the executable, relative to the plugin directory. Required for `stdio`. |
| `args` | no | Args appended to the executable command line (stdio). |
| `envPrefix` | no | Env-var prefix forwarded to the subprocess (stdio). Default: `BOWIRE_`. |
| `shutdownTimeoutMs` | no | Grace period after `shutdown` before SIGKILL (stdio). Default: `3000`. |
| `url` | http | The JSON-RPC endpoint (e.g. `http://localhost:7000/rpc`). Required for `http`. |
| `version` | no | Version string for `bowire plugin list` (sidecars have no NuGet version). |

## Transports

The same contract rides over two wires (like MCP's stdio + streamable-HTTP):

- **`stdio`** (default) — Bowire spawns `executable` as a local child
  process and speaks NDJSON JSON-RPC over its stdin/stdout. Zero-config,
  host-owned lifecycle, no network surface. Best for a plugin shipped as
  a zip and run on the same machine as the workbench.
- **`http`** — the sidecar is a (possibly remote) HTTP service at `url`.
  Requests are JSON-RPC envelopes **POST**ed to the endpoint (the HTTP
  response body carries the JSON-RPC reply); server-initiated
  notifications (`$/stream/data`, `$/channel/data`, …) stream back over
  one long-lived **SSE** `GET` on the same endpoint. Bowire does *not*
  own the process — disposing just closes the SSE stream and sends a
  best-effort `shutdown` the service may ignore. Suits hosted /
  multi-tenant deployments where one sidecar serves many hosts.

```json
{ "packageId": "Acme.Bowire.Protocol.Remote",
  "protocol": { "id": "remote", "name": "Remote" },
  "transport": "http",
  "url": "http://localhost:7000/rpc" }
```

The method surface below is identical on both transports.

## JSON-RPC method surface

Every method on `IBowireProtocol` maps 1:1 onto a JSON-RPC method. The
mapping is intentionally mechanical so per-language SDKs can be thin.

### `initialize` (host → sidecar, request)

Sent immediately after the process starts. Carries the host's
declared protocol id (so the sidecar can verify) and surfaces plugin
settings + iconSvg overrides.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "hostVersion":"1.7.0",
  "expectedProtocolId":"zenoh"
}}
```

Reply:

```json
{"jsonrpc":"2.0","id":1,"result":{
  "name":"Zenoh",
  "id":"zenoh",
  "iconSvg":"<svg...>",
  "settings":[]
}}
```

A failed `initialize` (id mismatch, missing dependency, etc.) returns
a JSON-RPC error; the host marks the plugin as unhealthy and surfaces
the error in `bowire plugin list --verbose`.

### `shutdown` (host → sidecar, request)

Asks the sidecar to clean up + exit. The host waits up to
`shutdownTimeoutMs` for the process to exit on its own, then kills it.

### `ping` (host → sidecar, request)

Liveness probe. Reply: `{"result":"pong"}`. Hosts may use this between
calls to detect a hung process before the next invocation timeouts
out.

### `discover` (host → sidecar, request)

Maps to `IBowireProtocol.DiscoverAsync`. Params: `{serverUrl,
showInternalServices}`. Reply: an array of `BowireServiceInfo` shapes
serialized as the same JSON the .NET plugins emit.

### `invoke` (host → sidecar, request)

Maps to `IBowireProtocol.InvokeAsync`. Params: `{serverUrl, service,
method, jsonMessages, showInternalServices, metadata}`. Reply: the
`InvokeResult` shape (`response`, `durationMs`, `status`, `metadata`).

### `invokeStream` (host → sidecar, request + notifications)

Maps to `IBowireProtocol.InvokeStreamAsync`. **The host generates the
`streamId`** and passes it in the request params, then subscribes
before the request goes out — so a sidecar that starts pushing data
immediately can't beat the host to the subscription. (Earlier drafts
had the sidecar mint the id and return it in the ack; host-generated
ids removed the subscribe-after-ack race.) The sidecar acks the
request, then emits zero or more notifications:

```json
{"jsonrpc":"2.0","method":"$/stream/data",
 "params":{"streamId":"...","message":"..."}}
```

…until it sends a terminator notification:

```json
{"jsonrpc":"2.0","method":"$/stream/end",
 "params":{"streamId":"...","error":null}}
```

`error` is `null` on clean completion or a JSON-RPC error object on
failure.

### `openChannel` / `channel.send` / `channel.close` / `$/channel/data` notifications

Map to `IBowireProtocol.OpenChannelAsync` + the `IBowireChannel`
surface — full duplex, both sides sending over a long-lived pipe.
Same host-generated-id convention as `invokeStream`: **the host mints
the `channelId`**, subscribes, then sends `openChannel` with it in the
params.

```json
// host → sidecar: open
{"jsonrpc":"2.0","id":7,"method":"openChannel",
 "params":{"channelId":"ab12","serverUrl":"...","service":"...","method":"...",
           "showInternalServices":false,"metadata":{}}}

// host → sidecar: send a frame (repeatable)
{"jsonrpc":"2.0","id":8,"method":"channel.send",
 "params":{"channelId":"ab12","message":"..."}}

// sidecar → host: inbound frame (zero or more, any time)
{"jsonrpc":"2.0","method":"$/channel/data",
 "params":{"channelId":"ab12","message":"..."}}

// host → sidecar: close the send side
{"jsonrpc":"2.0","id":9,"method":"channel.close",
 "params":{"channelId":"ab12"}}

// sidecar → host: channel fully closed (ends the read stream)
{"jsonrpc":"2.0","method":"$/channel/closed",
 "params":{"channelId":"ab12"}}
```

`channel.send` returns a JSON-RPC result (any value) on accept or an
error object when the channel is gone — the host maps that onto
`IBowireChannel.SendAsync` returning `true` / `false`. The host's
`SidecarChannel` treats every sidecar channel as full duplex
(`IsClientStreaming` = `IsServerStreaming` = true), since the
protocols that need a channel at all — WebSocket, chat-style pub/sub —
are duplex by nature.

## Lifecycle

1. **Spawn** — Bowire launches the executable with stdin / stdout
   piped, stderr inherited (so sidecar logs land in the host's
   console). The env is the host's env filtered by `envPrefix`.
2. **Initialize** — host sends `initialize`; sidecar replies with
   metadata. The plugin shows up in the workbench only after this
   completes.
3. **Steady state** — host issues `discover` / `invoke` /
   `invokeStream` / `openChannel` as the workbench drives them.
4. **Shutdown** — host sends `shutdown` on graceful host exit; gives
   the sidecar `shutdownTimeoutMs` to terminate; SIGKILLs (or
   `Process.Kill()` on Windows) on timeout.
5. **Crash recovery** — if the sidecar exits unexpectedly, the host
   treats the next `invoke` as a transport error (`Status` = the exit
   reason), and the **next** call respawns the process. No automatic
   exponential-backoff loop in Phase 1 — keeping the recovery model
   simple to debug.

## Why JSON-RPC and not gRPC?

- Stdio works on every OS without sockets or port-allocation pain.
- Hand-rolling a JSON-RPC client takes ~50 LOC in any language.
- gRPC needs protoc, codegen, a runtime — not realistic for plugin
  authors who want to write a 200-line Python script.
- The LSP / DAP / MCP precedent shows JSON-RPC scales to thousands of
  messages per second in steady-state.

## What sidecar plugins are NOT

- A way to extend the **host**. Sidecars implement protocols, not
  authentication providers, not UI extensions, not mock emitters.
  Those still ship as .NET assemblies (and likely always will — the
  surfaces are too .NET-shaped).
- A sandbox. The sidecar runs with the host's privileges. Treat
  installing a third-party sidecar with the same scrutiny as
  installing a third-party NuGet plugin.
- A streaming wire under the hood. JSON-RPC envelopes have non-zero
  serialization cost; if you need >10k msgs/sec sustained throughput,
  use a native .NET plugin.

## Per-language SDKs

Hand-rolling the JSON-RPC loop is ~50 LOC, but an SDK turns a plugin
into a five-method subclass. Available today:

- **Python** — [`Kuestenlogik/Bowire.Sdk.Python`](https://github.com/Kuestenlogik/Bowire.Sdk.Python)
  (`pip install bowire-plugin`): subclass `BowirePlugin`, implement
  `discover` / `invoke` / `invoke_stream`, call `run()`. Ships a
  runnable `examples/echo` plugin + `sidecar.json`.

Node.js / Go / Rust SDKs mirror the same surface — see the
[ROADMAP](../../ROADMAP.md).

## See also

- [`docs/protocols/custom.md`](../protocols/custom.md) — writing a
  custom .NET plugin (when you can stay in-ecosystem)
- The Phase 2+ roadmap entry in [`ROADMAP.md`](../../ROADMAP.md):
  remaining per-language SDKs + templates repo.

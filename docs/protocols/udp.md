---
summary: 'The UDP plugin binds to any UDP endpoint — multicast, broadcast, or unicast — and streams every received datagram into the workbench as a JSON envelope.'
---

# UDP

The UDP plugin binds to any UDP endpoint and streams every received datagram into the workbench as a JSON envelope. Useful for debugging any UDP-based protocol without a dedicated plugin: DIS (when you want raw bytes alongside [`Bowire.Protocol.Dis`](dis.md)'s typed decoding), NetFlow, syslog, game-server telemetry, custom sensor feeds, …

**Package:** `Kuestenlogik.Bowire.Protocol.Udp` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Udp
```

### Standalone

```bash
bowire --url udp://239.255.0.1:3000
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("udp://239.255.0.1:3000");
});
```

## URL shapes

```
udp://127.0.0.1:5514          # unicast — listen on loopback port 5514
udp://239.255.0.1:3000        # multicast — joins the group
udp://255.255.255.255:3000    # limited broadcast
udp://192.168.1.255:3000      # subnet-directed broadcast
udp://broadcast:3000          # shortcut for 255.255.255.255
udp://multicast:3000          # shortcut for 239.1.2.3
```

The transport mode is inferred from the IP address — multicast (`224.0.0.0/4`) joins the group, broadcast (`255.255.255.255` or any address ending in `.255`) enables `SO_BROADCAST`, everything else binds as unicast.

## Envelope

Each datagram arrives as:

```json
{
  "source": "10.0.0.5:53812",
  "bytes": 144,
  "text": "optional UTF-8 decoded string when the payload is valid UTF-8",
  "raw": "<base64 of the full datagram>"
}
```

`text` is absent (JSON `null`) when the payload contains non-UTF-8 bytes; the raw base64 is always present so the workbench can hex-dump it regardless.

## Mock replay

`UdpMockEmitter` plugs into the `bowire mock` server. Any recording with steps tagged `protocol: "udp"` gets re-broadcast on UDP at the original cadence (from `capturedAt`), honouring `MockEmitterOptions.Loop` / `.ReplaySpeed`.

Destination is read from the first UDP step's metadata, with sensible defaults:

| Metadata key | Purpose | Default |
|--------------|---------|---------|
| `destination` | Full `udp://host:port` URL (overrides `host`/`port`) | `udp://255.255.255.255:3000` |
| `host` / `port` | Alternative, set individually | `255.255.255.255` / `3000` |
| `ttl` | Multicast TTL; ignored for broadcast / unicast | `1` |

Transport mode (multicast join / `SO_BROADCAST` / plain unicast) is inferred from the destination IP, the same classifier the discovery side uses — a multicast recording replays as multicast, a broadcast recording as broadcast, and so on.

Payload source: `responseBinary` (base64 raw bytes) is preferred; otherwise the emitter falls back to `body` encoded as UTF-8, so text-only recordings (syslog, human-readable telemetry, …) still replay.

## Relationship to the DIS plugin

[`Bowire.Protocol.Dis`](dis.md) gives you typed DIS replay (entity discovery, PDU filtering, typed envelopes per IEEE 1278.1). UDP is the low-level cousin — it doesn't decode any protocol, it just surfaces bytes. Run both at once: DIS on one URL for typed per-entity streams, UDP on the same port for the raw-bytes view. Pick the protocol string (`dis` vs `udp`) on your recording steps to route them to the right emitter.

See also: [DIS](dis.md), [Recording](../features/recording.md), [Mock Server](../features/mock-server.md).

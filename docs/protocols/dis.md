---
summary: 'The DIS plugin discovers entities on an IEEE 1278 multicast group, streams typed PDU envelopes per entity (or for the whole exercise feed), and replays captured PDU sequences via the mock server.'
---

# DIS

The DIS plugin gives Bowire two paths into an **IEEE 1278.1 Distributed Interactive Simulation** multicast group:

- **Live discovery + entity-scoped streaming** — point at a `dis://group:port` URL and the workbench probes the group, surfaces every active entity as a service, and streams typed PDU envelopes (filtered to that entity, or to the whole exercise feed) into the workbench.
- **Mock replay** — `DisMockEmitter` re-broadcasts captured PDU sequences on UDP at the original cadence so simulation stacks can test against reproducible traffic without a live exercise.

**Package:** `Kuestenlogik.Bowire.Protocol.Dis` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Dis
```

### Standalone

```bash
bowire --url dis://239.1.2.3:3000
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("dis://239.1.2.3:3000");
});
```

## URL shapes

```
dis://239.1.2.3:3000          # standard multicast group + DIS port
dis://multicast               # shortcut for 239.1.2.3 (default port)
239.1.2.3:3000                # bare host:port also accepted
```

Default group is `239.1.2.3`, default port `3000` (IEEE 1278 convention). The probe duration during discovery is configurable via the plugin's `probeDuration` setting — defaults to 3 seconds, long enough to catch a heartbeat from every active entity.

## Live discovery

`DiscoverAsync` joins the multicast group for the probe window, observes every EntityState PDU it sees, and surfaces:

- one synthetic **`Exercise`** service whose `monitor` stream yields every PDU on the group (raw exercise feed)
- one service per discovered entity, named after its `EntityId` (`<site>:<application>:<entity>`) plus marking + entity-type info, whose `monitor` stream yields only PDUs from that entity

Subscribing to a service opens a UDP socket on the same group and yields one JSON envelope per matched PDU. The envelope decodes the PDU header (kind, family, exercise id, timestamp) plus EntityState specifics (position, orientation, velocity, marking string, force id) when applicable; other PDU kinds surface header + raw bytes so they can still be hex-dumped in the workbench.

## Mock replay

`DisMockEmitter` plugs into Bowire's mock server via `IBowireMockEmitter`. Recordings tagged `protocol: "dis"` get re-broadcast on the configured multicast group at the original cadence:

| Metadata key (first DIS step) | Purpose | Default |
|-------------------------------|---------|---------|
| `multicast-group` | UDP multicast destination | `239.1.2.3` |
| `port` | UDP port | `3000` |
| `ttl` | Multicast TTL — `1` keeps it on the local subnet | `1` |

Each step carries:

- `protocol: "dis"`
- `responseBinary` — base64 of the raw PDU bytes (IEEE 1278 wire format)
- `capturedAt` — millisecond timestamp used for emission pacing
- optional `metadata` (only on the first DIS step — applied to the whole DIS sub-sequence)

Replay is a thin relay — the emitter doesn't re-decode the PDUs, it ships the captured bytes verbatim. Use `bowire mock --recording my-exercise.bowire-recording.json --loop` for repeated replay.

## Relationship to the UDP plugin

[`Bowire.Protocol.Udp`](udp.md) is the low-level cousin — it surfaces every received datagram as a JSON envelope without decoding any protocol. Run both at once: DIS on the URL for typed per-entity streams, UDP on the same port for the raw-bytes view. Pick the protocol string (`dis` vs `udp`) on your recording steps to route them to the right emitter.

See also: [UDP](udp.md), [Recording](../features/recording.md), [Mock Server](../features/mock-server.md).

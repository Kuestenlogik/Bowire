---
summary: 'Sidecars gain gRPC as a third transport alongside stdio and HTTP. JSON-RPC over stdio stays the default — gRPC is chosen where native streaming and a typed contract matter more than a zero-dependency start.'
status: Proposed
date: 2026-08-24
---

# ADR-0002 — gRPC as an additional sidecar transport

> **Status:** Proposed · **Date:** 2026-08-24
> Supersedes nothing. [ADR-0001](0001-sidecar-plugins-speak-json-rpc.md) stands;
> this widens the choice rather than reversing it.

## Context

[ADR-0001](0001-sidecar-plugins-speak-json-rpc.md) chose JSON-RPC over gRPC
in May 2026, weighing the entry barrier for plugin authors above everything
else. That reasoning still holds for the case it was written for. Two things
have changed the picture around it.

**gRPC is now the default RPC stack in most of the languages sidecars are
written in.** It is first-class in Go, mature in Rust (`tonic`) and Python
(`grpcio`), and — contrary to the assumption that JavaScript would be the
holdout — `@grpc/grpc-js` has been pure JavaScript with no native build for
years. The "not realistic for plugin authors" argument has weakened
everywhere except at the very bottom of the effort scale.

**The streaming gap is real and was never closed.** Bowire's protocols are
streaming-heavy: server streams, channels, subscriptions. JSON-RPC has no
notion of a stream, so the implementation rebuilds one out of notifications
(`$/stream/data`, `$/channel/data`) plus a `SidecarSubscriptionHub`. It
works, but there is no flow control and no backpressure — a fast producer
has nothing to push back against. ADR-0001's own guidance to "use a native
.NET plugin above ~10k msgs/sec" is that gap, stated as a limit.

The SDKs make this cheaper than it looks. Their public surfaces are already
transport-neutral: Python exports `BowirePlugin`, the models, and
`run` / `run_http`, with JSON-RPC confined to the private `_runtime` /
`_http` modules; Rust exports `BowirePlugin` plus `runtime::stdio::run` and
`runtime::http::run_http`, and never makes `runtime::jsonrpc` public. A
plugin author implements `BowirePlugin` and picks an entry point.

## Decision

Add gRPC as a **third** `ISidecarTransport`, selected by `"transport":
"grpc"` in `sidecar.json`. JSON-RPC over stdio remains the **default** and
the documented starting point.

The choice is the plugin author's, per plugin:

- **stdio (JSON-RPC)** — the default. Nothing to install, works in any
  language that can write a line of JSON, no ports.
- **http (JSON-RPC)** — hosted or multi-tenant deployments, one sidecar
  serving many hosts.
- **grpc** — a typed contract with native bidirectional streaming, for
  plugins where throughput or stream semantics matter.

**Sequencing: this waits for #418.** The four language SDKs are already
behind the *current* contract. Adding a second transport to SDKs that have
not caught up with the first multiplies a debt that is already due, so #418
lands first.

## Alternatives considered

### Replace JSON-RPC with gRPC

Rejected. It breaks every existing sidecar, and it discards the property
that makes the mechanism worth having: today a sidecar can be written in
anything that emits JSON lines — a shell script, an exotic language, a
throwaway prototype. It would also buy the least where it costs the most:
for genuinely high-throughput plugins the native .NET path already beats any
out-of-process transport.

### Change nothing

Rejected, but it was close. The gap only bites plugins that stream heavily,
and those have a workaround. What tips it is that the cost of keeping the
door open is near zero — `ISidecarTransport` already has two
implementations and the manifest already carries a `transport` field, so
this is additive in the host. The cost lands in the SDKs, which is why the
sequencing above matters more than the decision itself.

## Consequences

- **The SDK maintenance surface doubles.** Four SDKs times two transport
  families. This is the real price, and #418 shows the failure mode: SDKs
  drifting behind the contract.
- **gRPC brings build-time dependencies that a transport-neutral API cannot
  hide.** `tonic` wants `protoc` at build time; `grpcio` ships large
  wheels. A Rust plugin author notices this when adding the dependency, not
  when writing code — so the SDK abstraction helps less here than it does
  for the wire format itself.
- **The zero-dependency path survives**, because stdio stays the default. A
  sidecar author who does not care about streaming performance never meets
  gRPC.
- **`docs/architecture/sidecar-plugins.md` stays unchanged until this
  ships.** Per the rule in [the ADR README](README.md), unimplemented design
  does not go into the Ist-Stand documentation — this record is where it
  lives in the meantime.

## Related

- [ADR-0001](0001-sidecar-plugins-speak-json-rpc.md) — the original transport choice, unchanged
- #611 — the implementation ticket for this decision (v3.0)
- #418 — re-sync the language SDKs; blocks #611
- `src/Kuestenlogik.Bowire/Plugins/Sidecar/ISidecarTransport.cs` — the seam this plugs into

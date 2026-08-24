---
summary: 'Out-of-process protocol plugins speak JSON-RPC 2.0 (NDJSON over stdio, or POST + SSE over HTTP) rather than gRPC, so a plugin can be a 200-line script in any language.'
status: Accepted
date: 2026-05-27
---

# ADR-0001 — Sidecar plugins speak JSON-RPC, not gRPC

> **Status:** Accepted · **Date:** 2026-05-27

## Context

Bowire's first-party protocol plugins are .NET assemblies implementing
`IBowireProtocol`. That works while the best client for a protocol ships as
a NuGet package — gRPC, REST, GraphQL, NATS.Net and DotPulsar all qualify.

It stops working when the canonical library lives in another ecosystem:
Zenoh is Rust, `paho-mqtt` and the SciPy/ML stack are Python, the Temporal
SDK is Go, `socket.io-client` is Node. Porting each of those to .NET was not
viable, and the alternative — declining to support the protocol — would have
capped the plugin surface at whatever the .NET ecosystem happened to cover.

So an out-of-process plugin was needed, and it needed a wire. The audience
for that wire is the constraint that decided everything below: someone who
wants to expose a protocol they already have a client for, ideally in a
couple of hundred lines, in a language of their choosing.

## Decision

A sidecar is an executable in any language that implements the
`IBowireProtocol` contract by speaking **JSON-RPC 2.0**. Two transports
carry the same method surface, mirroring MCP's stdio + streamable-HTTP pair:

- **`stdio`** (default) — Bowire spawns the executable as a child process
  and exchanges **NDJSON**: one JSON-RPC envelope per UTF-8 line terminated
  by `\n`. No `Content-Length` header, no chunking.
- **`http`** — the sidecar is a possibly remote service; requests are
  POSTed, and server-initiated notifications stream back over one long-lived
  SSE `GET` on the same endpoint.

A `sidecar.json` manifest at the root of the plugin directory marks it and
selects the transport.

## Alternatives considered

### gRPC

The obvious candidate for a typed, streaming, cross-language contract, and
rejected on the audience: gRPC needs `protoc`, code generation and a runtime.
That is a reasonable ask of a .NET plugin author and an unreasonable one for
someone writing a 200-line Python script — which is precisely the case the
sidecar mechanism exists to serve. Hand-rolling a JSON-RPC client, by
contrast, is roughly 50 lines in any language.

### Porting the client libraries to .NET

Would have kept everything in-process and typed. Not viable at the scale
required — it means owning a permanent port of every foreign-ecosystem
client, each tracking its upstream.

### A custom framing over sockets

Stdio works on every OS with no sockets and no port allocation. A bespoke
framing would have re-solved a problem LSP, DAP and MCP each solved the same
way, independently — which is also the strongest evidence that JSON-RPC
carries the load: those protocols sustain thousands of messages per second
in steady state.

## Consequences

A protocol plugin can now be written in any language and installed from a
zip, an HTTP URL or an OCI reference, using the same
`bowire plugin install` path as .NET plugins.

The costs are real and deliberate:

- **JSON-RPC envelopes have non-zero serialization cost.** Sustained
  throughput above ~10k msgs/sec should use a native .NET plugin.
- **Sidecars extend protocols only** — not auth providers, not UI
  extensions, not mock emitters. Those surfaces are too .NET-shaped and stay
  in-process.
- **A sidecar is not a sandbox.** It runs with the host's privileges, and
  installing a third-party one warrants the same scrutiny as a third-party
  NuGet plugin.

## Related

- [`docs/architecture/sidecar-plugins.md`](../architecture/sidecar-plugins.md) — how the shipped mechanism works today
- `src/Kuestenlogik.Bowire/Plugins/Sidecar/` — implementation

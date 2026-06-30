---
title: Deployment modes — Standalone Tool vs Embedded vs Interceptor-only
summary: 'Three ways to ship Bowire: the standalone dotnet tool, the embedded MapBowire() workbench, and the in-process middleware-only Interceptor. When each fits.'
---

# Deployment modes

## What this gets you

A clear decision rule for picking between the three shapes Bowire
ships in:

1. **Standalone Tool** — `dotnet tool install -g Kuestenlogik.Bowire.Tool`.
   Separate process, points at any URL.
2. **Embedded** — `builder.Services.AddBowire(); app.MapBowire();`
   inside your own ASP.NET host.
3. **Interceptor-only** —
   `app.UseBowireInterceptor()` without `MapBowire()`. In-process
   middleware that records every request through the host into a
   workbench surface you can reach from elsewhere (or skip entirely,
   if you only want the side-effect of capturing flows).

Everything below is anchored on existing source: the
[`samples/Kuestenlogik.Bowire.Sample.Embedded`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Embedded)
host, the
[`Kuestenlogik.Bowire.Interceptor`](https://github.com/Kuestenlogik/Bowire/tree/main/src/Kuestenlogik.Bowire.Interceptor)
package, and the standalone Tool documented in
[Standalone Tool](../setup/standalone.md).

## Mode 1 — Standalone Tool

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire --url https://your-server
```

The tool starts a local HTTP server on `http://localhost:5080`,
auto-opens a browser, and points the workbench at the target URL via
its loaded protocol plugins. The standalone Tool flips
`BowireOptions.Mode` to `BowireMode.Standalone` explicitly so the URL
bar is visible and the operator can add / edit / remove discovery
URLs at runtime.

When this fits:

- **Target service isn't yours.** Third-party APIs, vendor services,
  anything you can't drop a NuGet into.
- **Target isn't .NET.** Go / Rust / Python / Node services — see
  also the [Sidecar deployment](../setup/sidecar.md).
- **One-shot QA session.** Spin up `bowire --url https://...`, run
  through the methods, close it. No persistence beyond
  `~/.bowire/`.
- **CI fixture.** Pair with `bowire mock` to stand up a recorded
  service inside the test harness.

When this doesn't fit:

- You want the **in-process Interceptor** — the standalone Tool can't
  see the host's traffic without a separate MITM proxy
  (`bowire proxy`). Embedded mode + `UseBowireInterceptor()` is the
  zero-cert path.
- You want the workbench **gated by your existing auth pipeline** —
  the standalone Tool ships its own auth seam
  (`IBowireAuthProvider`); pairing it with the host's middleware is
  awkward.

See [Standalone Tool](../setup/standalone.md) for the full CLI
surface.

## Mode 2 — Embedded (`MapBowire()`)

```csharp
using Kuestenlogik.Bowire;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBowire();
var app = builder.Build();
app.MapBowire();      // workbench at /bowire
app.Run();
```

The workbench mounts as an endpoint group inside your own host.
Same process, same port, same auth pipeline. Default mode is
`BowireMode.Embedded` — the URL bar is hidden and services discovered
via the host's `IServiceProvider`.

When this fits:

- **You own the ASP.NET host** and want a workbench available during
  development.
- **You want the interceptor**.
  `app.UseBowireInterceptor()` shares the host's process — the rail
  surfaces traffic against the host's own endpoints with zero
  cert-trust dance, no separate process, no port juggling.
- **You want auth-share with the host.** Call
  `.RequireAuthorization("YourPolicy")` on the `MapBowire` return —
  the workbench inherits the host's scheme + policy. See
  [Lifecycle — Authentication](lifecycle.md#authentication).
- **You want richer discovery.** Embedded plugins receive the host's
  `IServiceProvider` via `IBowireProtocol.Initialize(IServiceProvider?)`,
  so gRPC reflection / SignalR hub enumeration / endpoint-metadata
  probing all work without going over the network.
- **You want the workbench surface on a non-standard URL.** Pass any
  prefix to `MapBowire("/api-browser")`.

When this doesn't fit:

- **Target service doesn't run ASP.NET.** The standalone Tool is
  for that.
- **You want a hardened production host with no extra surface.**
  Either gate aggressively (`.RequireAuthorization` + the
  `LockServerUrl` / `DisabledPlugins` knobs in `BowireOptions`), or
  switch to interceptor-only (Mode 3 below) and drop the workbench
  surface entirely.

See [Quickstart](quickstart.md) for the full walkthrough,
[Embedded mode setup](../setup/embedded.md) for per-protocol package
requirements, and
[Embedded-host customization](../architecture/embedded-host-customization.md)
for picking between `Kuestenlogik.Bowire.Bundle.Minimal` and
`Kuestenlogik.Bowire.Bundle.Workbench`.

## Mode 3 — Interceptor-only (middleware without `MapBowire()`)

You can reference `Kuestenlogik.Bowire.Interceptor`, register
`AddBowire()`, and call `UseBowireInterceptor()` **without** mounting
the workbench. Every inbound request still flows into
`InterceptedFlowStore`; the captured flows are reachable
programmatically (or via a Bowire workbench mounted somewhere else
that points at the same store, when the deployment shape allows it).

```csharp
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Interceptor;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBowire();   // covers InterceptedFlowStore registration via IBowireServiceContribution
var app = builder.Build();

app.UseBowireInterceptor();     // capture every inbound request
// ... your endpoints ...
// no app.MapBowire() — no workbench surface

app.Run();
```

What the in-process interceptor does, anchored on
[`BowireInterceptorMiddleware`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire.Interceptor/BowireInterceptorMiddleware.cs)
and [`BowireInterceptorOptions`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire.Interceptor/BowireInterceptorOptions.cs):

- Captures method, path, query, headers, request body, response
  status, response headers, response body, latency, and timestamp per
  flow.
- Bodies cap at `MaxBodyBytes` (default 1 MiB) per side.
- Ring buffer caps at `MaxRetainedFlows` (default 1000, FIFO
  eviction).
- `IgnoredPathPrefixes` defaults to `["/bowire"]` so the workbench
  surface (when mounted) doesn't observe itself. Extend the list to
  mute noisy health-check routes.
- `Enabled` master kill-switch short-circuits with no body buffering /
  stream wrapping / store write when flipped to `false`.
- `MocksEnabled` (default `true`) lets `InterceptorMockStore` rules
  short-circuit matching inbound requests with a canned response;
  free when the rule set is empty.

When this fits:

- **You want the side-effect of capturing flows** for audit / debug /
  recording without exposing the workbench UI on the host.
- **The workbench lives elsewhere** — e.g. a separate dev host
  mounts `MapBowire()` and reads recordings exported from production.
- **You want mock-injection capability** without the workbench UI on
  the production host.

For the full feature page see
[Interceptor](../features/proxy.md) — the rail UI, the SSE stream at
`/api/intercepted/stream`, the Append-to-recording flow, the mock
rules.

## Decision tree

```
Is the target service yours?
├── No  → Standalone Tool (`dotnet tool install -g Kuestenlogik.Bowire.Tool`).
└── Yes
    │
    Is it an ASP.NET host?
    ├── No  → Standalone Tool, optionally as a Sidecar container.
    └── Yes
        │
        Do you want the workbench UI on the host itself?
        ├── No   → Interceptor-only (`UseBowireInterceptor()` without `MapBowire()`).
        └── Yes  → Embedded (`AddBowire()` + `MapBowire()`,
                    optionally + `UseBowireInterceptor()`).
```

## Trade-offs at a glance

| Concern | Standalone Tool | Embedded `MapBowire()` | Interceptor-only |
|---|---|---|---|
| Process boundary | Separate process | In-process | In-process |
| Auth pipeline | Bowire's own (`IBowireAuthProvider`) | Host's existing middleware | Host's existing middleware |
| Discovery seam | Network (URL bar visible) | Host's `IServiceProvider` (URL bar hidden) | n/a (no UI) |
| Can intercept host's traffic | No (use `bowire proxy` for MITM) | Yes, with `UseBowireInterceptor()` | Yes |
| Workbench UI mounted | Yes, at `/` on `localhost:5080` | Yes, at `/bowire` (default) | No |
| Cert-trust dance | None | None | None |
| `BowireOptions.Mode` | `Standalone` | `Embedded` | n/a (`MapBowire` not called) |
| Typical fit | Third-party APIs, QA, CI | Dev-time workbench for your own service | Production audit / capture without UI surface |

## Cross-links

- [Quickstart](quickstart.md) — Embedded mode walkthrough.
- [Options](options.md) — every `BowireOptions` knob.
- [Lifecycle](lifecycle.md) — what runs when.
- [Standalone Tool](../setup/standalone.md) — full CLI surface.
- [Sidecar deployment](../setup/sidecar.md) — non-.NET targets.
- [Interceptor feature](../features/proxy.md) — the rail surface fed
  by `UseBowireInterceptor()`.
- [Embedded-host customization](../architecture/embedded-host-customization.md)
  — picking by bundle.
- [Sample.Embedded](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Embedded)
  — canonical embedded host with all three layers
  (`AddBowire` + `UseBowireInterceptor` + `MapBowire`).

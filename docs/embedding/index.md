---
title: Embed Bowire
summary: 'What "embedded mode" means — Bowire as an ASP.NET workbench mounted inside your own host — and why you would pick it over the standalone Tool.'
---

# Embed Bowire

**Embedded mode** mounts the Bowire workbench as an ASP.NET endpoint
group inside your own service. One `builder.Services.AddBowire()` line
in `Program.cs` plus one `app.MapBowire()` line and your host now serves
the multi-protocol workbench under `/bowire` (default) — in the same
process, behind your existing authentication and CORS pipeline, with
access to the host's `IServiceProvider` for discovery.

Two sentences of positioning:

- **Embedded** = Bowire ships inside your service. Same process, same
  port, same auth pipeline. Auto-discovers what the host exposes and
  can intercept its own traffic via `app.UseBowireInterceptor()`
  ([`Kuestenlogik.Bowire.Interceptor`](../features/proxy.md)).
- **Standalone Tool** ([`Kuestenlogik.Bowire.Tool`](../setup/standalone.md))
  = a separate `dotnet tool` process pointed at a remote URL. No code
  change to the target. Use this when the target isn't yours or isn't
  .NET.

## What this section covers

| Page | Topic |
|---|---|
| [Quickstart](quickstart.md) | `dotnet new webapi` → `AddBowire()` → `MapBowire()` → browse `/bowire`. Walk-through of the `samples/Kuestenlogik.Bowire.Sample.Embedded` host, line by line. |
| [Options](options.md) | Every public property on `BowireOptions` — title, theme, `ServerUrls`, `DisabledPlugins`, `Mode`, `MapBasemap`, `SchemaHintsPath`, `DisableBuiltInDetectors`. Plus the `appsettings.json` keys that bind them. |
| [Lifecycle](lifecycle.md) | DI registration (`AddBowire`), endpoint mapping (`MapBowire`), startup ordering, plugin auto-discovery via the `IBowireProtocol` / `IBowireProtocolServices` / `IBowireServiceContribution` sweep, interaction with the host's authentication and routing. |
| [Deployment modes](deployment-modes.md) | Standalone Tool vs Embedded vs in-process middleware-only (the [Interceptor](../features/proxy.md)). Decision rules. |

## Two-line preview

```csharp
builder.Services.AddBowire();
app.MapBowire();   // workbench mounted at /bowire
```

See [Quickstart](quickstart.md) for the full walkthrough with a working
sample, [Options](options.md) for every knob, and [Lifecycle](lifecycle.md)
for how Bowire wires itself into the host's pipeline.

## See also

- [Deployment modes overview](../setup/index.md) — the existing
  three-mode comparison (Embedded / Standalone / Docker).
- [Embedded mode setup](../setup/embedded.md) — per-protocol package
  requirements (gRPC reflection, OpenAPI document, SignalR hubs…).
- [Embedded-host customization](../architecture/embedded-host-customization.md)
  — picking by bundle (`Bundle.Minimal` vs `Bundle.Workbench`) or
  per-package opt-in.
- [Sample.Embedded](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Embedded)
  — the canonical embedded host, referenced from every page in this
  section.
- [API reference](../api/index.md) — `MapBowire`, `AddBowire`,
  `BowireOptions`, `UseBowireInterceptor`.

---
title: Build a Settings module
summary: 'Implement IBowireModuleContribution to add a cross-cutting capability (AI chat pane, variable resolver, guided tour) that hooks the workbench shell without owning a rail icon.'
---

# Build a Settings module

A **module** is a cross-cutting capability that hooks the workbench across multiple surfaces but doesn't own a left-strip icon. The AI module wires a chat pane into every rail. The variable-resolver module patches the URL bar + request builder. The Assistant module hooks the topbar overflow. Hosts that don't ship the package shouldn't see any trace of the module in the UI — that's the whole point of pulling them through descriptors.

Reach for this seam when your package augments existing surfaces; reach for [a rail](rail.md) when your package brings a new top-level activity.

## The interface

`IBowireModuleContribution` lives in `src/Kuestenlogik.Bowire/Plugins/IBowireModuleContribution.cs`. The public surface is intentionally narrow:

```csharp
public interface IBowireModuleContribution
{
    string Id { get; }
    string DisplayName { get; }
    string Description => string.Empty;
    bool DefaultEnabled => true;
}
```

What each member does:

- **`Id`** — stable identifier (e.g. `"ai"`, `"assistant"`, `"var-resolver"`). Surfaced to the JS bundle through `__BOWIRE_CONFIG__.modules` so module-aware render paths can opt into the module's hooks only when it's loaded.
- **`DisplayName`** — human-readable label shown in Settings → Modules.
- **`Description`** — one-sentence description shown under the label. Tells the operator what surface they're turning on or off. Defaults to empty.
- **`DefaultEnabled`** — defaults to `true`. Most modules should stay on by default — operators expect the thing they explicitly installed to work. Set `false` only when the footprint (network, disk, perf) is heavy enough that opt-in is the right default.

That's the whole descriptor. A module is a marker: "this capability is installed, surface its hooks." The real wiring — DI services, hosted services, endpoint mounts, JS render branches — lives elsewhere in your package and references the module id when it needs to gate behaviour on the operator's toggle.

## Minimal working example

The AI module (`src/Kuestenlogik.Bowire.Ai/BowireAiModuleContribution.cs`) is the simplest in-repo template — and was the first module extracted into a per-package descriptor:

```csharp
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Ai;

public sealed class BowireAiModuleContribution : IBowireModuleContribution
{
    public string Id => "ai";
    public string DisplayName => "AI Assistant";
    public string Description
        => "Chat-with-your-API panel, schema-grounded hints, and assistant drawer toggle.";
}
```

That's the descriptor in full. The actual AI runtime + endpoints + JS hooks live elsewhere in the same `Kuestenlogik.Bowire.Ai` package: `BowireAiRuntime` owns the `IChatClient`, `BowireAiServiceCollectionExtensions.AddBowireAi(IConfiguration, …)` is the explicit DI opt-in, and `BowireAiEndpoints` mounts the HTTP surface. Hosts that reference `Kuestenlogik.Bowire.Ai` see the AI hooks wired into the workbench shell; hosts that don't reference the package don't pay for any of the AI-specific JS render branches because the descriptor never shows up in `__BOWIRE_CONFIG__.modules`.

## Wiring the rest of the package

The module descriptor only opts your capability into the Settings catalogue. To actually do work you'll likely also implement one or both of these companion contributions in the same package:

- **`IBowireServiceContribution`** (`src/Kuestenlogik.Bowire/Plugins/IBowireServiceContribution.cs`) — auto-discovered at `AddBowire()` time. Implement `void ConfigureServices(IServiceCollection services)` to register your DI services without making Core take a compile-time reference on your package. Used by the Welle 2 Interceptor package; see `BowireInterceptorServiceContribution` for the reference impl.
- **`IBowireEndpointContribution`** (`src/Kuestenlogik.Bowire/Plugins/IBowireEndpointContribution.cs`) — auto-discovered at `MapBowire()` time. Implement `void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)` to mount your HTTP / SSE / WebSocket endpoints inside Core's auth-gated route group. A host that opted into `AddBowireAuth(...)` automatically gates your endpoints too.

Both contributions also require a parameterless constructor and tolerate exceptions silently (the protocol-discovery posture) so one misbehaving sibling can't take down host startup.

## Registration

Two paths, mirroring rails:

**1. Auto-discovery (the common case).** Drop the contribution type into a `Kuestenlogik.Bowire.*` assembly with a parameterless constructor. `BowireModuleRegistry.Discover` (in `src/Kuestenlogik.Bowire/Plugins/BowireModuleRegistry.cs`) walks every loaded `Kuestenlogik.Bowire*` assembly, picks every concrete `IBowireModuleContribution` implementation, instantiates it via `Activator.CreateInstance`, and registers it.

**2. Explicit registration.** When the descriptor lives in an assembly the auto-scan won't reach, or to override a built-in's metadata (registry de-duplicates by `Id`, last write wins):

```csharp
builder.Services.AddBowireModule<MyCustomModule>();
```

`AddBowireModule<TModule>()` is on `BowireServiceCollectionExtensions` and requires `TModule : class, IBowireModuleContribution, new()`.

The registry surfaces every registered module through `BowireModuleRegistry.ToJson()`, which seeds `__BOWIRE_CONFIG__.modules`: `{ id, label, description, defaultEnabled }`. JS render paths gate on `__BOWIRE_CONFIG__.modules.find(m => m.id === 'ai')` (or equivalent) before opening their module-specific surfaces.

## See also

- <xref:Kuestenlogik.Bowire.Plugins.IBowireModuleContribution> — auto-generated interface reference.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireServiceContribution> — DI registration at `AddBowire()` time.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireEndpointContribution> — endpoint mounting at `MapBowire()` time.
- [Plugin architecture](../architecture/plugin-architecture.md) — the rail / module / endpoint / service contribution split.
- [Build a rail](rail.md) — when your package brings a new top-level activity instead of augmenting existing surfaces.

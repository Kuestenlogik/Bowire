# Embedded host customization

> Audience: developers embedding Bowire (`MapBowire()`) into an existing
> ASP.NET Core app, or writing a from-scratch host that ships Bowire as
> its workbench. End users who run the `bowire` CLI don't have to do any
> of this — `Bowire.Tool` brings the whole workbench surface in by
> default.

Since #306 every rail (Home, Discover, Compose, Recordings, Mocks,
Flows, Proxy, Intercepted, Benchmarks, Workspaces, Collections,
Environments, Security) and every cross-cutting module (AI assistant)
ships as its own NuGet package. Embedded hosts opt into the surface
they want by picking packages.

## Picking by bundle (quickstart)

Three meta-packages ship today. Pick one as a `<PackageReference>` and
you get its transitive set of rails + modules + protocols:

| Bundle | What's in it | When to pick it |
|---|---|---|
| `Kuestenlogik.Bowire.Bundle.Minimal` | core + Compose + REST + gRPC | Just-fire-a-request hosts — no workbench surface beyond Compose. No Security, no AI, no Mocks/Recordings/Flows/Proxy/Benchmarks. |
| `Kuestenlogik.Bowire.Bundle.Workbench` | core + every Rail + every Module + every Protocol + Security + AI + Help + Telemetry + Workspace.Git + Mock + Mcp + AsyncApi | The everything-on superset; what `Bowire.Tool` ships. Pick this if you want the same out-of-the-box experience as the standalone CLI but embedded inside your app. |
| _no bundle_ | DIY per-package opt-in | When you want a non-standard surface — e.g. Discover + Security only (a security-team workbench). |

```xml
<!-- Embedded host that wants the full workbench. -->
<ItemGroup>
  <PackageReference Include="Kuestenlogik.Bowire.Bundle.Workbench" />
</ItemGroup>
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBowire();
// every rail registered (workbench bundle pulled them all in),
// AI module is available, every protocol plugin is in the registry
var app = builder.Build();
app.MapBowire(prefix: "/bowire");
app.Run();
```

## DIY per-package opt-in

If neither bundle fits, drop the bundle reference and pick rail +
module + protocol packages by hand. Every rail package follows the
same shape — adding a `PackageReference` to the rail package makes the
rail appear in `__BOWIRE_CONFIG__.rails`; dropping it makes the rail
disappear (no UI surface, no settings checkbox, no JS bundle bloat).

Available rail packages:

- `Kuestenlogik.Bowire.Rail.Home`
- `Kuestenlogik.Bowire.Rail.Discover`
- `Kuestenlogik.Bowire.Rail.Compose` _(hosts the canonical Collections + Presets side panel; the standalone `Rail.Collections` package was retired in v2.1)_
- `Kuestenlogik.Bowire.Rail.Environments` _(hidden from rail strip)_
- `Kuestenlogik.Bowire.Rail.Recordings`
- `Kuestenlogik.Bowire.Mock` _(carries both the mock-host runtime AND the Mocks rail descriptor + fragment; the provisional standalone `Rail.Mocks` package was folded in for v2.1)_
- `Kuestenlogik.Bowire.Rail.Flows`
- `Kuestenlogik.Bowire.Rail.Proxy`
- `Kuestenlogik.Bowire.Rail.Intercepted`
- `Kuestenlogik.Bowire.Rail.Benchmarks`
- `Kuestenlogik.Bowire.Rail.Workspaces`
- `Kuestenlogik.Bowire.Security.Scanner` _(carries the Security rail descriptor + the Nuclei scanner runtime)_

Available module packages:

- `Kuestenlogik.Bowire.Ai` (Ollama / LM Studio out of the box; carries the AI module descriptor)
- `Kuestenlogik.Bowire.Ai.OpenAi` (OpenAI / OpenRouter)
- `Kuestenlogik.Bowire.Ai.Anthropic` (Claude)
- `Kuestenlogik.Bowire.Ai.Mcp` (MCP-as-gateway)

Worked example — a security-team workbench that only wants Discover +
Security:

```xml
<ItemGroup>
  <PackageReference Include="Kuestenlogik.Bowire" />
  <PackageReference Include="Kuestenlogik.Bowire.Rail.Discover" />
  <PackageReference Include="Kuestenlogik.Bowire.Security.Scanner" />
  <!-- Pick the protocols this host needs to probe. -->
  <PackageReference Include="Kuestenlogik.Bowire.Protocol.Rest" />
  <PackageReference Include="Kuestenlogik.Bowire.Protocol.GraphQL" />
</ItemGroup>
```

No `Bundle.Workbench`, no Recordings / Mocks / Flows / Proxy /
Benchmarks / AI. The rail strip renders only Discover + Security
because the descriptors for the other rails are never discovered.

## Always-on rails

A handful of rails ship `AlwaysOn = true` — these can't be turned off
via Settings → Rail modes if their package is referenced. They are
intentionally minimal:

- Home
- Discover
- Compose
- Workspaces

If you want a host without _any_ of those, drop the package — the
rail vanishes entirely. `AlwaysOn` only controls the Settings toggle
inside the discovered catalogue.

## Runtime toggle (per operator)

For rails + modules that ARE installed, the operator can still disable
them at runtime via Settings → Rail modes / Modules (persisted to
`localStorage`). Always-on rails render greyed out with a "Built-in"
badge.

## Phase G continuation — per-package JS fragments (#311)

Five of the heaviest per-rail JS slices now live on their rail packages
as embedded resources, not in core's `wwwroot/js/`. `BowireHtmlGenerator`
scans every loaded `Kuestenlogik.Bowire.Rail.*` assembly at HTML-emit
time, pulls the JS resources matching `*.wwwroot.js.*.js`, and stitches
their content into the assembled `bowire.js` between the
`/*BOWIRE_RAIL_FRAGMENTS_BEGIN*/` and `/*BOWIRE_RAIL_FRAGMENTS_END*/`
markers core ships inside its IIFE. The stitched-in code therefore
shares core's closure scope, so the existing bare-identifier references
into helpers / state / renderers keep resolving without any
window-namespace dance.

Shipped in this phase:

| Rail package | JS fragment | LOC moved |
|--------------|-------------|----------:|
| `Kuestenlogik.Bowire.Rail.Recordings` | `recording.js` | ~1700 |
| `Kuestenlogik.Bowire.Mock` _(was `Rail.Mocks` pre-v2.1)_ | `mocks.js` | ~600 |
| `Kuestenlogik.Bowire.Rail.Flows` | `flows.js` | ~1700 |
| `Kuestenlogik.Bowire.Rail.Compose` | `compose-rail.js` | ~1200 |
| `Kuestenlogik.Bowire.Rail.Intercepted` | `intercepted-view.js` | ~700 |

Together, ~6,000 lines (≈200 KB pre-minify) drop out of every
`Bundle.Minimal` bundle that doesn't opt into the matching rail.
Hosts that DO reference the rail package see the same JS surface as
today — the splice is byte-for-byte identical to the old monolithic
concat, only the source-of-truth moved.

Deferred to a follow-up ticket (Phase G remainder): the per-rail
branches inside `render-sidebar.js` and `render-main.js`, plus
`proxy-view.js`, `benchmarks.js`, `collections.js`. These have
cross-rail dispatchers that need a small descriptor-driven hook
(`IBowireRailContribution.RenderSidebar(state)` &c.) before they can
cleanly leave core — out of scope for #311 but unblocked by the
stitching machinery it lands.

## See also

- [`docs/architecture/plugin-architecture.md`](./plugin-architecture.md) — the descriptor registry pattern
- [`docs/architecture/packages.md`](./packages.md) — what every package does
- [`docs/architecture/ai-integration.md`](./ai-integration.md) — picking AI provider packages

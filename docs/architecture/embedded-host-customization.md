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
- `Kuestenlogik.Bowire.Rail.Compose`
- `Kuestenlogik.Bowire.Rail.Collections` _(default-off; the standalone full-pane editor — Compose rail's side panel is the primary surface)_
- `Kuestenlogik.Bowire.Rail.Environments` _(hidden from rail strip)_
- `Kuestenlogik.Bowire.Rail.Recordings`
- `Kuestenlogik.Bowire.Rail.Mocks`
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

## Phase G continuation — JS fragment concat

The Phase G follow-up (tracked in a separate ticket) extracts the
per-rail JS render code (sidebar template, main-pane rendering) out of
core `wwwroot/js/` into each rail package's embedded resources, so a
host without the rail package also doesn't ship the rail's JS to the
browser. Today the descriptor moves are physical (drop the package,
drop the rail from the catalogue) but the JS for every rail still
ships in the core bundle. Functionally identical for the
opt-in / opt-out experience — only the bundle byte-count is still
core-sized.

## See also

- [`docs/architecture/plugin-architecture.md`](./plugin-architecture.md) — the descriptor registry pattern
- [`docs/architecture/packages.md`](./packages.md) — what every package does
- [`docs/architecture/ai-integration.md`](./ai-integration.md) — picking AI provider packages

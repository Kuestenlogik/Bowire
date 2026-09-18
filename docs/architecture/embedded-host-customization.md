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

Available rail / activity packages (v2.1 #325 dropped the
`Rail.` prefix from every package id; foundational Home + Discover
rails were folded into Core):

- `Kuestenlogik.Bowire` — carries the descriptor-only Home + Discover rails (folded into Core in v2.1)
- `Kuestenlogik.Bowire.Compose` — hosts the canonical Collections + Presets side panel; the standalone `Rail.Collections` package was retired in v2.1
- `Kuestenlogik.Bowire.Workspaces` — workspace switcher AND the workspace-scoped Environments rail (the `Rail.Environments` package was folded into Workspaces in v2.1)
- `Kuestenlogik.Bowire.Recordings`
- `Kuestenlogik.Bowire.Mock` _(carries both the mock-host runtime AND the Mocks rail descriptor + fragment; the provisional standalone `Rail.Mocks` package was folded in for v2.1)_
- `Kuestenlogik.Bowire.Flows`
- `Kuestenlogik.Bowire.Interceptor` — the unified Proxy + Intercepted + Traffic rails AND the interceptor middleware, reverse-proxy host, /api/intercepted endpoints (the `Rail.Proxy` + `Rail.Intercepted` + `Rail.Traffic` packages were consolidated in v2.1; embedded hosts that drop this package lose the interceptor stack entirely — no middleware, no rails, no admin endpoints)
- `Kuestenlogik.Bowire.Benchmarking` _(was `Rail.Benchmarks` until v2.1; the gerund matches the activity-rail naming pattern)_
- `Kuestenlogik.Bowire.Security.Scanner` _(carries the Security rail descriptor + the Nuclei scanner runtime)_
- `Kuestenlogik.Bowire.Help` _(carries the Help rail descriptor + the in-app docs renderer)_

Available module packages:

- `Kuestenlogik.Bowire.Ai` (Ollama / LM Studio out of the box; carries the AI module descriptor)
- `Kuestenlogik.Bowire.Ai.OpenAi` (OpenAI / OpenRouter)
- `Kuestenlogik.Bowire.Ai.Anthropic` (Claude)
- `Kuestenlogik.Bowire.Ai.Mcp` (MCP-as-gateway)

Worked example — a security-team workbench that only wants Discover +
Security:

```xml
<ItemGroup>
  <!-- Discover ships inside Core since v2.1 (#325); no extra package
       reference needed. -->
  <PackageReference Include="Kuestenlogik.Bowire" />
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
scans every loaded `Kuestenlogik.Bowire.*` assembly at HTML-emit
time (the v2.1 #325 cleanup dropped the `Rail.` prefix from every
package id; the discovery filter widened to match every Bowire-
namespaced sibling), pulls the JS resources matching
`*.wwwroot.js.*.js`, and stitches their content into the assembled
`bowire.js` between the `/*BOWIRE_RAIL_FRAGMENTS_BEGIN*/` and
`/*BOWIRE_RAIL_FRAGMENTS_END*/` markers core ships inside its IIFE.
The stitched-in code therefore shares core's closure scope, so the
existing bare-identifier references into helpers / state / renderers
keep resolving without any window-namespace dance.

Every package that owns JS fragments today, measured against the
working tree (source bytes, UTF-8):

| Package | JS fragment(s) | Lines | KB |
|---|---|---:|---:|
| `Kuestenlogik.Bowire.Map` _(asset endpoint, not stitched — `extensions.js` loads it on demand)_ | `widgets/map.js` | 4,438 | 204 |
| `Kuestenlogik.Bowire.Benchmarking` | `benchmarks.js`, `benchmark-schedules.js` | 3,836 | 182 |
| `Kuestenlogik.Bowire.Recordings` | `recording.js`, `recording-correlation.js` | 3,605 | 171 |
| `Kuestenlogik.Bowire.Flows` | `flows.js` | 2,745 | 131 |
| `Kuestenlogik.Bowire.Ai` | `ai.js` | 2,358 | 113 |
| `Kuestenlogik.Bowire.Interceptor` | `intercept-view.js`, `intercepted-view.js`, `proxy-view.js`, `tools-reverse-proxy.js` | 2,327 | 107 |
| `Kuestenlogik.Bowire.Compose` | `compose-rail.js` | 1,643 | 76 |
| `Kuestenlogik.Bowire.Mock` | `mocks.js` | 1,265 | 68 |
| `Kuestenlogik.Bowire.SchemaDesigner` | `schema-designer.js` | 854 | 34 |
| `Kuestenlogik.Bowire.Security.Scanner` | `security.js` | 492 | 26 |
| `Kuestenlogik.Bowire.Help` | `help.js` | 465 | 20 |
| `Kuestenlogik.Bowire.Monitoring` | `monitoring.js` | 327 | 14 |
| `Kuestenlogik.Bowire.Contracts` | `contract-matrix.js` | 211 | 8 |
| **Total** | | **24,566** | **1,155** |

## Shipped bundle size by package selection

Core's built `wwwroot/bowire.js` is the baseline every host pays:
**3,966 KB**. Each package above adds its fragments on top, and only when
the host references it.

| Selection | Bundle (source bytes) |
|---|---:|
| `Bundle.Minimal` (REST + gRPC only — no package owns a JS fragment) | ~3,966 KB |
| `Bundle.Minimal` + Recordings + Mock | ~4,205 KB |
| `Bundle.Workbench` (all 13 packages) | ~4,917 KB |

`Bundle.Workbench` does reference `Kuestenlogik.Bowire.Map`, but Map's
204 KB is **not** in that figure: its widget JS ships through an asset
endpoint that `extensions.js` fetches on demand, not through the splice.

Two caveats on reading these numbers:

* **These are source bytes, not transfer bytes.** The build's `MinifyFile`
  step is not a minifier despite the name — comment stripping was disabled
  because the `//` regex could not tell a comment from a `https://` URL,
  so all it does now is collapse blank-line runs and strip trailing
  whitespace. Measured against the fragments, that is under 1%. What
  actually shrinks the payload is the response compression in front of it,
  which these figures do not model.
* **The baseline is not a floor.** Core still carries Discover-rail and
  layout code (`collections.js`, `presets.js`, `catalogue.js`,
  `coverage.js`, `perf-diff.js`, `shelf.js`, …) that is cross-cutting
  rather than rail-owned. Those belong to core by design, not leftovers.

To re-measure after adding a fragment, `wc -c` the files under each
package's `wwwroot/js/` and core's built `wwwroot/bowire.js`.

## Adding a fragment to a package

1. Put the `.js` under `<Package>/wwwroot/js/`.
2. Declare it as an `EmbeddedResource` whose `LogicalName` is
   `<AssemblyName>.wwwroot.js.<file>.js` — the generator matches on
   `.wwwroot.js.` and the `.js` suffix, so the name has to be spelled
   out rather than left to the default.
3. Make sure **every** core reference into the fragment is guarded
   (`typeof fn === 'function'`, or `typeof x !== 'undefined'` for
   module-scope state). A bare identifier read throws `ReferenceError`
   in a host that doesn't ship the package — the fragment's `var`
   declaration is gone, not merely unassigned.
4. Fragments are spliced in **last**, right before `init.js`. Function
   declarations still hoist across the shared closure, but module-scope
   `var` *assignments* now run after every core fragment has loaded, so
   a fragment cannot be relied on for load-time state.

## See also

- [`docs/architecture/plugin-architecture.md`](./plugin-architecture.md) — the descriptor registry pattern
- [`docs/architecture/packages.md`](./packages.md) — what every package does
- [`docs/architecture/ai-integration.md`](./ai-integration.md) — picking AI provider packages

# Test coverage gap report — v2.1 audit

_Generated 2026-06-26 from `dotnet test Kuestenlogik.Bowire.slnx --collect "XPlat Code Coverage"` and `node --test tests/Kuestenlogik.Bowire.Tests/wwwroot-js/`._

This report is recon for the v2.2 "Test pillar" track. It maps every v2.1
closed ticket to its source files and the coverage we currently have on
them, ranks the gaps by risk, and proposes an attack order for the next
wave. **No new tests are written by this pass.** Subsequent agents pick
gaps off this list.

## Totals

| Track | Number |
| --- | --- |
| C# test count (all `*.Tests` projects, all green) | **1 427** pass / 0 fail |
| C# line coverage (cobertura, merged) | **87.0 %** |
| C# branch coverage | **77.2 %** |
| C# method coverage (full) | **80.4 %** |
| JS test files | **2** (`history-replay.test.mjs`, `script-sandbox.test.mjs`) |
| JS test count | **22** pass / 0 fail |
| JS modules in `src/Kuestenlogik.Bowire/wwwroot/js/` | **45** (1 is the `_morphdom.js` vendored 1-liner concat header) |
| JS modules **without any test** | **43 of 44** (~ 97 %) |

The C# headline is healthy (87 % lines, 77 % branches) but the
distribution is uneven: a handful of v2.1-introduced endpoints sit well
below 60 %. The JS side has almost no coverage at all — two modules out
of forty-four have direct tests, and the two that do (`render-main.js`
event-handler resolution and `scripts.js` sandbox semantics) were
written reactively after recent bugs.

## Coverage per v2.1 closed ticket

v2.1 closed milestone: **60 issues**. The table groups them by surface;
"polish/UX-only" tickets that only changed CSS, copy, icons, or
rendering of an already-tested code path are bucketed together at the
bottom — they are not the primary risk surface for v2.2.

| Ticket | Surface | Source path(s) | Line cov | Status | Effort |
| --- | --- | --- | --- | --- | --- |
| **#136** URL / service catalogue providers (local/http/consul/k8s/agent) | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowireCatalogueEndpoints.cs`; `src/Kuestenlogik.Bowire/Sources/*CatalogueProvider.cs`; `src/Kuestenlogik.Bowire.Catalogue.*`; `wwwroot/js/catalogue.js` | endpoint **13.3 %**, providers 81–86 %, JS **0 %** | gap | large |
| **#140** Per-mode 'Saved Configs' / Presets | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowirePresetEndpoints.cs`; `src/Kuestenlogik.Bowire/PresetStore.cs`; `wwwroot/js/presets.js` (604 LOC) | endpoint **23.8 %**, store 89 %, JS **0 %** | gap | medium |
| **#295** Compose rail: integrate Collections + Presets | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowireCollectionEndpoints.cs`; `src/Kuestenlogik.Bowire/CollectionStore.cs`; `wwwroot/js/collections.js`, `wwwroot/js/compose-rail.js` (1.2k LOC) | endpoint **34.2 %**, store 100 %, JS **0 %** | partial | medium |
| **#153** Bowire as transparent interceptor — embedded + reverse-proxy | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowireInterceptorEndpoints.cs`; `src/Kuestenlogik.Bowire/Interceptor/*.cs`; `wwwroot/js/intercepted-view.js`, `proxy-view.js` | endpoint **52.2 %**, middleware 80–98 %, JS **0 %** | partial | medium |
| **#144** Large-recording capture/replay — chunked disk layout | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowireRecordingEndpoints.cs`; `src/Kuestenlogik.Bowire/ChunkedRecordingStore.cs`; `wwwroot/js/recording.js` (1.7k LOC) | endpoint **59 %**, store 95 %, JS **0 %** | partial | medium |
| **#194** Action log Phase 2 — workspaces soft-delete + cross-reload undo | C# + JS | `src/Kuestenlogik.Bowire/Endpoints/BowireWorkspaceEndpoints.cs`; `wwwroot/js/render-sidebar.js`, `render-main.js` | endpoint **54.6 %**, JS **0 %** | partial | medium |
| **#285** Lift active recording state from localStorage into BowireRecordingSession | C# | `src/Kuestenlogik.Bowire/Recording/BowireRecordingSession.cs`; `src/Kuestenlogik.Bowire/Endpoints/BowireRecordingSessionEndpoints.cs` | session 100 %, endpoint **75.2 %** | covered | small |
| **#287** Dual-MCP endpoint: MapBowireMcp + MapBowireMcpAdapter | C# | `src/Kuestenlogik.Bowire.Mcp/*.cs`; `src/Kuestenlogik.Bowire.Protocol.Mcp/*.cs` | 90–96 % | covered | small |
| **#286** `mcp serve --attach` MCP-over-MCP forwarder | C# | `src/Kuestenlogik.Bowire.Mcp/BowireForwardingMcpTransport.cs`; `src/Kuestenlogik.Bowire/App/McpServeCommand.cs` | forwarder **89 %**, command **63.5 %** | partial | small |
| **#126** Pre-/post-scripts with protocol-typed sandbox | JS-only | `wwwroot/js/scripts.js`; sandbox is JS, no C# host | JS sandbox: **22 tests** in `script-sandbox.test.mjs` cover REST/gRPC/MQTT shapes, assert API, ring-buffer, lint | **covered** | n/a |
| **#145** Deprecate `${name}` for `{{name}}` (multi-phase) | C# + JS | `src/Kuestenlogik.Bowire/...VariableResolver`; `wwwroot/js/vars-*.js` (1.2k LOC across vars-autocomplete, vars-chips, vars-deprecation) | resolver 100 % (Nuclei), JS **0 %** | partial | small-medium |
| **#234** Benchmark result exports (CSV / k6 / OTLP) | C# + JS | `src/Kuestenlogik.Bowire/...`; `wwwroot/js/benchmarks.js` (3.5k LOC), `perf-diff.js` (891 LOC) | C# touchpoints high; JS **0 %** | partial | medium |
| **#233** Benchmarks: previous-run diff banner (p95 ▲/▼) | JS | `wwwroot/js/perf-diff.js` (891 LOC) | JS **0 %** | gap | medium |
| **#231** Benchmarks: 'random' run shape | C#+JS | `wwwroot/js/benchmarks.js` | JS **0 %** | partial | small |
| **#300** Benchmark: mode-switch becomes inert after first switch | JS | `wwwroot/js/benchmarks.js` | JS **0 %** | gap | small |
| **#302** Response viewer: line numbers + collapsible JSON + breadcrumb + raw + download | JS | `wwwroot/js/render-main.js` (9.7k LOC) | partial via render-main data-attr tests | gap | medium |
| **#289** Hopp-bar single-line request: method + URL + params + execute | JS | `wwwroot/js/request-builder.js` (3k LOC), `request-builder-protocols.js` (1.1k) | JS **0 %** | gap | medium |
| **#291** Hopp-bar: protocol picker (REST / gRPC / MQTT / WS / SSE / GraphQL / MCP) | JS | `wwwroot/js/request-builder-protocols.js` | JS **0 %** | gap | small-medium |
| **#293** New 'Design' rail for ad-hoc requests | C#+JS | `src/Kuestenlogik.Bowire.Rail.Compose/BowireComposeRailContribution.cs`; `wwwroot/js/compose-rail.js`, `request-builder.js` | rail 100 % (trivial), JS **0 %** | gap | medium |
| **#266–#268** Postman-style URL+verb+body / freeform validate / URL is call URL | JS | `wwwroot/js/request-builder.js`, `compose-rail.js` | JS **0 %** | gap | small-medium |
| **#280** Assistant hints: actionable inline buttons | JS | `wwwroot/js/render-main.js` | n/a — UI snippet emit, low-risk | partial | small |
| **#281** Guided tour: page-navigation + spotlight | JS | `wwwroot/js/tour.js` (1.0k LOC) | JS **0 %** | gap | medium |
| **#296** Topbar: global Trash drawer + Undo/Redo | JS | `wwwroot/js/render-main.js`, `render-sidebar.js` | JS **0 %** | gap | medium |
| **#297** Topbar: responsive overflow collapse into ⋮ | JS+CSS | `wwwroot/js/render-main.js`; `wwwroot/bowire.css` | n/a — CSS/layout | covered-enough | none |
| **#310** UI: Settings → Modules toggle | JS | `wwwroot/js/settings.js` (3.2k LOC) | JS **0 %** | partial | small |
| **#263** Settings tree: list every enabled plugin | JS | `wwwroot/js/settings.js` | JS **0 %** | partial | small |
| **#309** UI: configure URL catalogue providers from Settings | JS | `wwwroot/js/settings.js`, `catalogue.js` | JS **0 %** | partial | small |
| **#248** Optional rail modules — toggle + always-on + Settings editor | C#+JS | `src/Kuestenlogik.Bowire/...ModuleRegistry`; `wwwroot/js/settings.js` | C# 90 %, JS **0 %** | partial | small |
| **#282** Unified `.bww` format: UI export + CLI export converge | C#+JS | `src/Kuestenlogik.Bowire/App/Cli/WorkbenchRecordingJsonProvider.cs` (86 %); UI export in render-main | partial | medium |
| **#242** User-defined workspace templates | C#+JS | `src/Kuestenlogik.Bowire/Endpoints/BowireWorkspaceEndpoints.cs`; `wwwroot/js/workspace-templates.js` (645 LOC) | endpoint **54.6 %**, JS **0 %** | partial | small-medium |
| **#243** Failed REST/gRPC logs show status + body + exception | JS | `wwwroot/js/execute.js`, `request-builder.js` | JS **0 %** | gap | small |
| **#254** Freeform builder: auto-discover prompt after first successful invoke | JS | `wwwroot/js/compose-rail.js`, `request-builder.js` | JS **0 %** | gap | small |
| **#259 / #257** richErrorDetail picks up problem+json status / type / instance | JS | `wwwroot/js/execute.js`, `render-main.js` | JS **0 %** | gap | small |
| **#260** Console toolbar: distinct icons | JS | `wwwroot/js/render-main.js` | n/a — purely glyph swap | n/a | none |
| **#261** Tab persistence for 'As new request' freeform clone | JS | `wwwroot/js/compose-rail.js` | JS **0 %** | gap | small |
| **#262** Force-home rule retired — rails clickable again after delete-last-workspace | JS | `wwwroot/js/render-sidebar.js`, `render-main.js` | JS **0 %** | gap | small |
| **#264 / #265** Save-as-template icon fix + apostrophe JS parser bug | JS | `wwwroot/js/render-main.js` | regression risk; needs a guard test | gap | small |
| Workspace UX polish: **#270 / #271 / #273 / #274 / #275 / #276 / #277 / #278 / #279** | JS | `wwwroot/js/render-sidebar.js`, `render-main.js`, `landing.js` | UX/CSS, partial JS state transitions | covered-enough | none |
| Topbar / hint polish: **#272 / #297 / #301** | JS+CSS | various | covered-enough | none |
| Home + landing polish: **#301** | JS+CSS | `wwwroot/js/landing.js` | covered-enough | none |
| Discover/Design split: **#244 / #245 / #246** | JS | `wwwroot/js/render-main.js`, `compose-rail.js` | JS **0 %** | partial | small-medium |
| Embedded mode polish: **#299** Proxy rail in embedded mode | C# | proxy endpoints 88 % | covered | none |
| Deprecation: **#197** MapLibre nuget unlist | infra | n/a — release machinery | n/a | none |
| Flows: **#298** protocol+service selection populates dropdowns | JS | `wwwroot/js/flows.js` (1.7k LOC) | JS **0 %** | gap | small-medium |

### Low-coverage C# classes worth a dedicated test pass

These are the classes flagged by the cobertura merge below 70 %, scoped
to v2.1 surface. (Pure log/options classes at 0 % are by design — no
behaviour to cover — and are excluded.)

| Class | Line cov | Why it matters |
| --- | --- | --- |
| `BowireCatalogueEndpoints` | **13.3 %** | #136 catalogue providers — POST/PUT/DELETE/override paths untested |
| `BowirePresetEndpoints` | **23.8 %** | #140 per-mode presets — mutate paths untested |
| `BowireCollectionEndpoints` | **34.2 %** | #295 Compose integration — mutate paths untested |
| `BowirePluginEndpoints` | **46.2 %** | Plugin install/manifest/uninstall paths |
| `BowireInterceptorEndpoints` | **52.2 %** | #153 reverse-proxy + embedded middleware — config paths untested |
| `BowireInvokeEndpoints` | **53.0 %** | Core invoke endpoint — error/timeout branches untested |
| `BowireWorkspaceEndpoints` | **54.6 %** | #194 / #242 — soft-delete, templates, undo branches untested |
| `BowireRecordingEndpoints` | **59.0 %** | #144 / #282 — chunked load, export, .bww round-trip |
| `BowireSecurityEndpoints` | **66.2 %** | scan execute paths |
| `App.McpServeCommand` | **63.5 %** | #286 attach forwarder — CLI error paths |
| `BowireAuthEndpoints` | **71.3 %** | refresh / logout / cookie-revoke branches |

## Recommended attack order

The plan is _critical paths first, then edge cases, then style fixes_, scoped to v2.1 surface.

### Wave 1 — endpoint coverage to ≥ 80 % (high-value, mostly small/medium)

These are server-side mutation endpoints where the consequences of a
bug are user data loss or silent corruption. They are all .NET, so the
test infrastructure already exists (`Kuestenlogik.Bowire.IntegrationTests`
uses WebApplicationFactory) and net-new fixtures are minimal.

1. **`BowireCatalogueEndpoints` — 13.3 → 80+** (large): #136 — 5 provider types, override store, conflict resolution. Add to `tests/Kuestenlogik.Bowire.IntegrationTests/` mirroring `BowireRecordingSessionEndpointTests`.
2. **`BowirePresetEndpoints` — 23.8 → 80+** (medium): #140 — POST/PUT/DELETE per-mode preset round-trips.
3. **`BowireCollectionEndpoints` — 34.2 → 80+** (medium): #295 — collections + folder nesting + import-from-source.
4. **`BowireWorkspaceEndpoints` — 54.6 → 80+** (medium): #194, #242 — soft-delete + restore-from-trash + template apply.
5. **`BowireInterceptorEndpoints` — 52.2 → 80+** (medium): #153 — embedded vs reverse-proxy config switch, mock-rule mutation.
6. **`BowireRecordingEndpoints` — 59 → 80+** (medium): #144, #282 — chunked load, `.bww` round-trip, partial-recording resume.

### Wave 2 — JS test harness + first round (large infra, then small/medium per gap)

The JS side has essentially no coverage. Two test files exist, both
using `node --test` with in-source ES modules. To scale we need a tiny
harness that:

- exposes a JSDOM-style document mock _or_ asserts on the pure data
  shapes (the existing tests prefer the second style — they import the
  module under test and call exported functions; render assertions
  spot-check `outerHTML` of fragments returned by helpers).
- runs from `tests/Kuestenlogik.Bowire.Tests/wwwroot-js/` so the
  `package.json` (none today) or `node --test` glob is the unit of
  CI work.

Order, once the harness lands:

7. **`scripts.js` regression net** — already covered by 22 tests in
   `script-sandbox.test.mjs`; pin it with one assertion per published
   surface so #126 doesn't regress.
8. **`vars-deprecation.js` + `vars-autocomplete.js` + `vars-chips.js`** (#145):
   1.2k LOC of variable-resolver migration logic.
9. **`benchmarks.js` + `perf-diff.js`** (#231, #233, #234, #300): 4.4k
   LOC, includes the regression for #300 (mode-switch becomes inert).
10. **`request-builder.js` + `request-builder-protocols.js` + `compose-rail.js`** (#266–#268, #289, #291, #293, #254, #261): 5.3k LOC, the Hopp-bar + Compose-rail bedrock.
11. **`catalogue.js`** (#136 + #309): only 100 LOC but currently 0 % — pure JS-side feature.
12. **`execute.js` + `render-main.js` error-path slice** (#243, #257, #259, #265): rich-error detail, problem+json parsing, the apostrophe-bug regression test from #265.
13. **`recording.js` + `intercepted-view.js`** (#144, #153): chunked-load UI + interceptor flow view.
14. **`settings.js` + `workspace-templates.js`** (#248, #263, #310, #242): settings tree, modules toggle.
15. **`flows.js`** (#298): protocol+service dropdown population.
16. **`tour.js`** (#281): page-navigation + spotlight transitions.

### Wave 3 — edge-case + regression-net

17. Catalogue providers (Agent, Kubernetes): bump `Catalogue.Agent` from 72 % and `Catalogue.Kubernetes` from 76 % by exercising the `ServiceCollectionExtensions` (currently 0 % — they only register DI, but they are the public extension surface; one smoke test each).
18. `BowireInvokeEndpoints` 53 % — error/timeout/cancellation branches.
19. `Mcp.BowireMcpChatClient` 56.8 % (#286) — forwarder failure paths.
20. UX polish tickets (#270/#271/#273/#274/#275/#276/#277/#278/#279,
    #297, #260, #264) — skipped from test wave unless a specific
    regression bites; they are CSS / glyph swaps with no logic worth
    pinning.

## Method

```bash
dotnet test Kuestenlogik.Bowire.slnx \
  --collect "XPlat Code Coverage" \
  --results-directory artifacts/coverage

reportgenerator \
  -reports:"artifacts/coverage/*/coverage.cobertura.xml" \
  -targetdir:"artifacts/coverage/report" \
  -reporttypes:"HtmlSummary;TextSummary;MarkdownSummaryGithub"

node --test tests/Kuestenlogik.Bowire.Tests/wwwroot-js/*.test.mjs
```

`coverlet.runsettings` is the shared exclusion config — generated code,
source-gen output, `[GeneratedCode]` / `[CompilerGenerated]` /
`[GeneratedRegex]` are stripped before coverage is computed, so the
percentages above reflect _authored_ code.

## Out of scope for this report

- v2.0 and earlier features (the 87 % aggregate already buries these).
- v2.2+ tickets (those are the consumer of this report, not the subject).
- Cyclomatic / cognitive complexity — orthogonal axis, not asked for.
- Mutation testing — would be the natural follow-up once endpoint line
  coverage clears 80 %.

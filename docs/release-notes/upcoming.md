---
title: <fill in before the tag>
version: 2.2.0
---

<One-sentence frame for what 2.1 is about. Replaces this placeholder
the moment the first 2.1 work lands.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern:
### <headline> (#issue)
<2-4 sentences>
-->

### Bowire as a transparent in-process interceptor — `app.UseBowireInterceptor()` (#153)

The new interceptor middleware extends Bowire from "the operator drove a call" to "every request flowing through the host." `app.UseBowireInterceptor()` registers a pass-through middleware that records method / path / headers / request body / response status / response headers / response body / latency for every request the host receives — from any client, with zero client-side setup, no cert trust, no separate process. Captured flows land in the workbench's new **Intercepted** rail (sister to the standalone Proxy rail) live over SSE. When the operator starts a recording in the workbench, intercepted flows auto-append as recording steps — point any client at the host, click stop, replay. Standalone reverse-proxy mode (Phase C) and mock injection (Phase D) ship in later releases.

### MCP-over-MCP forwarder — `bowire mcp serve --attach` (#286)

A thin Bowire process can now relay every incoming MCP tool call to a heavier Bowire running on the operator's workstation. `bowire mcp serve --attach localhost:5198 --port 5199` boots a forwarder that surfaces no local tools — `tools/list`, `tools/call`, prompts, resources, and resource templates are all marshalled to the parent and the parent's response is relayed verbatim. Useful when an LLM agent on a CI runner / container should drive the workstation Bowire without sharing the parent's MCP socket directly. The parent gains a matching `--token <secret>` bearer-auth gate (`--bind http` only); the child passes the secret with `--attach-token <secret>`.

### Workspace-delete Undo decoupled from Trash (W2a)

The action-log entry for `workspace-delete` is now self-contained: it carries the full snapshot (`workspace`, `data`, `originalIdx`) inline so Undo reads from the entry itself instead of looking the entry up in `workspacesTrash` at undo-time. This gives the two surfaces independent retention models:

- **Trash drawer** — operator-curated retention via `bowire_trash_retention_days` (W2).
- **Action log** — sliding window of the last 200 entries.

Hard-delete (W2 mode) now writes ONLY the action-log snapshot — there's no trash entry to consult. Soft-delete writes both. Entries persisted BEFORE this change still resolve via the legacy Trash-lookup path (a one-shot `console.info` flags the fallback so the migration window is visible).

The other entity-delete resolvers (`recording-delete`, `flow-delete`, `env-delete`) were audited and already carry their entity payload inline — only `workspace-delete` had the Trash coupling.

### Settings tree organized by scope; Configure + Plugins merged (#325)

The Settings dialog now exposes scope through the parent-node names: `This machine` (localStorage / install-scoped) and `Workspace…` (`.bww`-scoped, travels with the workspace file). The per-page disclaimer that used to repeat the scope copy on every sub-page (`"These settings stay on this machine — they don't travel with the workspace file (.bww)."`) is gone — the tree's grouping carries that information unambiguously. The standalone `Plugins` lifecycle leaf is gone; it has merged with `Configure` into a single `Plugins` node whose six sub-pages (Protocols / UI Widgets / Modules / Formats / Tools / Discovery providers) now render the inline per-plugin settings AND the lifecycle button cluster (Restart / Unload / Reset storage, wired to the existing `POST /api/plugins/{id}/lifecycle/{action}` 501-stub) per row, plus Install + Check-for-updates at the top of the Protocols sub-page. A new empty-state `Workspace… → Per-Workspace overrides` page surfaces every machine-scoped setting that the active workspace has overridden (today: the AI provider config, when the workspace has its own `ai-config.<workspaceId>.json`); saved deep-links from v2.1 (`configure-protocols`, `plugin-<id>`, `extension-<id>`) keep working unchanged, and any saved value pointing at the dropped `plugins` lifecycle id migrates to `configure-protocols`.

### Snapshot testing in the flow runner — capture-once, diff-on-change (#171)

A flow step can now carry `"snapshot": { "mode": "exact" | "structural", "ignore": ["$.ts", …] }`. The first `bowire test` run captures the response as a baseline under `__snapshots__/<flow>/<step>.snap.json` (checked into the repo next to the flow file); every later run diffs against it and fails with the drifted JSON paths. `ignore` marks dynamic fields (timestamps, generated ids) whose values may vary — their kind is still checked. `--update-snapshots` re-baselines after an intended change.

### `bowire test` speaks SARIF 2.1.0 and GitHub annotations (#181)

Alongside `--report` (HTML) and `--junit`, the runner now writes `--sarif path.sarif` for upload to GitHub Code Scanning, and `--annotations` emits `::error` workflow commands so failed expectations surface inline on the PR diff.

### Data-driven flow steps — inline / CSV / generator (#174)

A flow step can now carry a `data` source and run once per row: an inline JSON array, a CSV file (path resolved relative to the flow file, RFC-4180 quoting), or a deterministic generator (`range`, or `random` with a seed — same seed, same rows, on every .NET version). Row columns join the `{{var}}` resolver scope and shadow `--env` values of the same name. Each row reports as `stepId[label]` (pick the label column via `labelColumn`), so JUnit / SARIF / HTML reports group the parameterisation as one step family. Zero-row sources and expansions beyond 100 000 rows fail loudly as step errors instead of passing vacuously or hanging CI.

```json
{
  "id": "get-user",
  "type": "request",
  "serverUrl": "https://api.example.com/users/{{userId}}",
  "data": {
    "csv": "fixtures/users.csv",
    "labelColumn": "userId"
  },
  "expectations": [
    { "kind": "status", "operator": "equals", "expected": "200" },
    { "kind": "body-path", "operator": "exists", "target": "$.id" }
  ]
}
```

The UI editor for data sources on collection items and per-row pass/fail counts in the workbench are tracked separately on #174.

## Breaking changes

<!-- Add a section per breaking change, with the migration path. -->

### Mocks + Traffic rails merged into a single Intercept rail

The Mocks rail and the Traffic rail (which itself unified the earlier Proxy + Intercepted rails) have collapsed into one **Intercept** rail with four sub-tabs in a locked order:

- **Captured** — passive observation of flows captured by `UseBowireInterceptor()` (was Traffic → "Flows").
- **Live overrides** — selective response substitution inside the interceptor pipeline (was Traffic → "Mock Rules").
- **Mock servers** — standalone mock-server-from-recording hosts (was the entire Mocks rail).
- **Settings** — interceptor / proxy config (was Traffic → "Settings"); adapts to Standalone vs Embedded deployment.

The merge eliminates the previous Phase-3 rail bloat where Mocks + Traffic + the already-hidden Intercepted + Proxy descriptors competed for the same conceptual surface ("what do I do with live traffic"). It also disambiguates "Flows" — the orchestration rail keeps the name; what Traffic used to call "Flows" is now "Captured".

**Migration is automatic at first paint** (idempotent). On boot, `prologue.js`:

- Rewrites `localStorage.bowire_rail_mode` from `mocks` / `traffic` / `proxy` / `intercepted` → `intercept`, and seeds the sub-tab discriminator (`bowire_intercept_sub_tab`) based on the legacy mode:
  - `mocks` → `mock-servers`
  - `traffic` with sub-view `flows` → `captured`
  - `traffic` with sub-view `mocks` → `live-overrides`
  - `traffic` with sub-view `settings` → `settings`
  - `proxy` / `intercepted` → `captured`
- Rewrites `localStorage.bowire_sidebar_view` (`mocks` / `traffic` / `proxy` / `intercepted` → `intercept`).
- Collapses any `mocks` / `traffic` entry in `localStorage.bowire_enabled_rails` into a single `intercept`.

**Embedded hosts** that referenced the deleted descriptors must update their DI registrations:

- `BowireTrafficRailContribution` → `BowireInterceptRailContribution`
- `BowireProxyRailContribution`, `BowireInterceptedRailContribution`, `BowireMocksRailContribution`, `BowireEnvironmentsRailContribution` → no replacement; the descriptors are gone (the Environments surface continues to render inside Workspaces, and Mocks now lives inside the Intercept rail's Mock servers sub-tab).

`.bww` workspace files are unaffected — they don't persist rail mode.

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

## Breaking — TacticalApi retired from Bundle.Workbench (v2.2)

`Kuestenlogik.Bowire.Protocol.TacticalApi` is now opt-in. Operators
who need the Rheinmetall Situation service install it explicitly:

```
dotnet add package Kuestenlogik.Bowire.Protocol.TacticalApi
```

or, once the plugin marketplace ships, via **Settings → Plugins →
Install**. Bundle.Workbench stays the universal-web-protocol set
(REST, gRPC, GraphQL, MQTT, WebSocket, SSE, MCP, SignalR, JSON-RPC,
OData, Socket.IO). Domain-specific protocols follow the opt-in
pattern.

Migration for existing operators: nothing to do if you didn't use
TacticalApi. If you did — a one-line `dotnet add package` restores
the surface.

Related upstream fix: `Bowire.Protocol.TacticalApi` v1.0.4 now
gates its `DiscoverAsync` on the `tacticalapi@` URL scheme prefix,
so it can no longer surface Situation methods for unrelated
sources like a plain Petstore OpenAPI URL.

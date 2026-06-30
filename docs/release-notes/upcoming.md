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

### Workspace deletion: per-workspace cleanup is now exhaustive (W1)

The hard-delete path (`Delete forever` on a workspace entry in the Trash drawer) used to leak per-workspace state on two axes:

- **localStorage** — several buckets read/written through `wsKey()` (`bowire_history`, `bowire_recent_methods`, `bowire_ad_hoc_requests`, `bowire_catalogue_draft`, `bowire_url_headers`, `bowire_global_vars`, `bowire_enabled_modules`, `bowire_parallel_defaults_v1`, plus the per-workspace UI flags `bowire_favorites_only`, `bowire_auto_discover_asked`, `bowire_vars_dollar_snoozed`) were missing from the canonical key lists, so a purge spliced the Trash array but left those entries behind.
- **Disk storage** — per-workspace recording chunks under `~/.bowire/workspaces/<id>/` were never wiped. Hard-deleting a workspace with gigabytes of recordings reclaimed zero disk space.

Both leaks are closed: the canonical key lists now name every `wsKey()`'d bucket the audit found (verified by grep), and a new `DELETE /api/workspace/{workspaceId}` endpoint wipes the disk folder recursively. The endpoint is standalone-only (embedded hosts refuse with the same policy as the existing `open-folder` sibling — refusing to delete server-side bytes from a host that doesn't own its filesystem).

Two related fixes ship at the same time:

- `deleteWorkspace`'s in-memory cache reset used to fire only on the no-active-workspace branch. Non-last-workspace deletes left the deleted workspace's content visible in the surviving workspace's view until the next reload — now the reset runs on every active-workspace delete and the surviving workspace's localStorage namespace gets re-read inline so the rails render the right content immediately.
- Workspace name-collision check now ALSO sweeps the Trash bucket — creating a workspace with a name that lives in Trash surfaces a distinct toast (`A workspace named X is in the trash — restore it or purge it from the trash first`) instead of a misleading "already exists" against an invisible record.

### Settings tree organized by scope; Configure + Plugins merged (#325)

The Settings dialog now exposes scope through the parent-node names: `This machine` (localStorage / install-scoped) and `Workspace…` (`.bww`-scoped, travels with the workspace file). The per-page disclaimer that used to repeat the scope copy on every sub-page (`"These settings stay on this machine — they don't travel with the workspace file (.bww)."`) is gone — the tree's grouping carries that information unambiguously. The standalone `Plugins` lifecycle leaf is gone; it has merged with `Configure` into a single `Plugins` node whose six sub-pages (Protocols / UI Widgets / Modules / Formats / Tools / Discovery providers) now render the inline per-plugin settings AND the lifecycle button cluster (Restart / Unload / Reset storage, wired to the existing `POST /api/plugins/{id}/lifecycle/{action}` 501-stub) per row, plus Install + Check-for-updates at the top of the Protocols sub-page. A new empty-state `Workspace… → Per-Workspace overrides` page surfaces every machine-scoped setting that the active workspace has overridden (today: the AI provider config, when the workspace has its own `ai-config.<workspaceId>.json`); saved deep-links from v2.1 (`configure-protocols`, `plugin-<id>`, `extension-<id>`) keep working unchanged, and any saved value pointing at the dropped `plugins` lifecycle id migrates to `configure-protocols`.

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

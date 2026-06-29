---
title: <fill in before the tag>
version: 2.1.0
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

### Settings tree organized by scope; Configure + Plugins merged (#325)

The Settings dialog now exposes scope through the parent-node names: `This machine` (localStorage / install-scoped) and `Workspace…` (`.bww`-scoped, travels with the workspace file). The per-page disclaimer that used to repeat the scope copy on every sub-page (`"These settings stay on this machine — they don't travel with the workspace file (.bww)."`) is gone — the tree's grouping carries that information unambiguously. The standalone `Plugins` lifecycle leaf is gone; it has merged with `Configure` into a single `Plugins` node whose six sub-pages (Protocols / UI Widgets / Modules / Formats / Tools / Discovery providers) now render the inline per-plugin settings AND the lifecycle button cluster (Restart / Unload / Reset storage, wired to the existing `POST /api/plugins/{id}/lifecycle/{action}` 501-stub) per row, plus Install + Check-for-updates at the top of the Protocols sub-page. A new empty-state `Workspace… → Per-Workspace overrides` page surfaces every machine-scoped setting that the active workspace has overridden (today: the AI provider config, when the workspace has its own `ai-config.<workspaceId>.json`); saved deep-links from v2.1 (`configure-protocols`, `plugin-<id>`, `extension-<id>`) keep working unchanged, and any saved value pointing at the dropped `plugins` lifecycle id migrates to `configure-protocols`.

## Breaking changes

<!-- Add a section per breaking change, with the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

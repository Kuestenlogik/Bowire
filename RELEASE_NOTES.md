# Bowire Release Notes

**This file is generated.** Edit the release body on GitHub instead:
https://github.com/Kuestenlogik/Bowire/releases

The script `scripts/ci/generate-release-notes.mjs` pulls every published
release (and optionally drafts via `--include-drafts`) and writes
the body out here so the notes are readable offline. Use the
GitHub Release UI or `gh release edit <tag>` to change the editorial
text; re-run the generator to refresh the mirror.

---
## v2.0.0 — 2026-06-21 — Re-architected workbench shell + workspace = project folder

v2.0 is the cut where every optional surface gets pulled out of the core, the workbench shell gets re-architected end-to-end, and a couple of API-level decisions get unwound now that they have stable replacements. Embedded hosts that vendored `Kuestenlogik.Bowire` directly will need a small set of explicit changes — see **Breaking changes** at the bottom.

## Highlights

### Workbench shell re-architected (#115)

The workbench surface that ships in `bowire` and embedded hosts was redrawn from the structural level up — Topbar carries identity + workspace context, the left rail holds mode switching, the sidebar holds per-mode lists, the main pane holds the editor, the new statusbar holds system state. Tabs cohere visually into their panes, empty-states are uniform across every surface, and a shared `renderDrawer` primitive backs Assistant / Help / Tests / Activity / future Inspector — drawer chrome is no longer hand-rolled per surface. Single-Accent + Protocol-Glyph is the only colour encoding; sharper radii (2/4/6 px) come from CSS custom properties. Sidebar means navigation; URL/Schema-Files/AI-Settings became their own surfaces.

### Workspace = project folder (#147–#151 + #196 Phase 2)

`bowire workspace init / export / import / migrate-format` makes a workspace a real directory you can commit. Per-entity files (one JSON per request / environment / collection / recording) survive merges; secrets live in a sibling `secrets/` tree that's `.gitignore`d by default. The `Kuestenlogik.Bowire.Workspace.Git` runtime (shipped in this release) plumbs the per-entity layout through the workbench at read + write time, watches the directory for external edits via `FileSystemWatcher` + SSE so the workbench reload-toast fires when a teammate commits over your shoulder, merges `<env>.json` with its `<env>.secrets.json` sibling at resolve-time so the secret split stays a load-time concern instead of a save-time worry, and gates concurrent edits across two open Bowire instances via a stale-pid-aware `.bowire.lock`. For disk-mode workspaces (recordings / collections / scripts live on disk rather than in `localStorage`) the new `workspace export <file.json>` + `workspace import` CLI verbs capture every per-entity file in a single archive — `.bww`-style state download stays available for browser-mode workspaces.

### Workspace integrity — plugin pins + scope-split settings (#193)

A workspace can now pin the protocol plugins it expects (`workspace.pluginPins: { rest: ">=2.0", grpc: "*", mqtt: "5.x" }`). When a team member opens the workspace, a banner fires if any pinned plugin isn't loaded — with "Install all" and "Open editor" actions instead of cryptic "no such protocol" errors at first request. The pin editor lives in the workspace's own Settings tab, so pins ride the workspace into git instead of getting buried in per-user state. The Settings dialog itself grows a scope-split tree: "My preferences" (per-user — Assistant config, theme, shortcuts, plugins) vs "This project" (per-workspace — pins, sources, env-inclusion, AI override). The Assistant tab supports per-workspace overrides directly: a scope chip in Settings, a matching reminder chip on the workspace surface, and one-click "Use global instead" to drop the override.

### Optional packages live outside core

Core `Kuestenlogik.Bowire` ships with **zero `PackageReference` entries** — only the `Microsoft.AspNetCore.App` framework reference. Embedded hosts add only what they actually use:

- **`Kuestenlogik.Bowire.Extension.MapLibre` → `Kuestenlogik.Bowire.Map`** — the v1.3.0-rc.1 of the MapLibre extension will be deprecated + unlisted on nuget.org after 2.0 ships (#197).
- **OpenTelemetry extracted into `Kuestenlogik.Bowire.Telemetry`** — embedded hosts that don't want self-observability no longer pull the OTel transitive weight. The standalone CLI keeps `--telemetry` working because the tool transitively references it.
- **`.Ai`, `.Help`** stay opt-in as before. Standalone `bowire` bundles them.

Naming convention going forward — optional first-party packages are `Kuestenlogik.Bowire.<feature>` (`.Ai`, `.Help`, `.Telemetry`, `.Map`) or `Kuestenlogik.Bowire.<area>.<backend>` (e.g. `Kuestenlogik.Bowire.Workspace.Git`, shipped in this release). The legacy `.Extension.*` prefix isn't used for new packages.

### OpenAPI library decoupled via adapter packages

REST plugin no longer takes a hard dependency on `Microsoft.OpenApi`. A new `IBowireOpenApiAdapter` seam in `Kuestenlogik.Bowire.Protocol.Rest` lets the consumer pick the library version:

- **`Kuestenlogik.Bowire.Protocol.Rest.OpenApi2`** — pins `Microsoft.OpenApi` 2.x, matches what ASP.NET Core 10's `AddOpenApi()` transitively pulls. Standalone `bowire` bundles this one.
- **`Kuestenlogik.Bowire.Protocol.Rest.OpenApi3`** — pins `Microsoft.OpenApi` 3.x for hosts that want the modern grammar.

Both can be loaded side-by-side; the adapter registry auto-picks the one matching the runtime's `Microsoft.OpenApi` major. Resolves the OpenAPI-DLL-version conflict reported on .NET 10 hosts running side-by-side with ASP.NET's bundled discovery.

### Across-the-pane omnibox (#124 / #162)

Cmd/Ctrl+K opens a single search line that ranks methods, recordings, collections, settings, and `?`-prefixed AI prompts uniformly. Replaces the per-surface searches that used to live in the sidebar, drawer, and method picker.

### Action log + Ctrl+Z (#168) and hint dismiss (#169)

Every destructive action (delete recording, delete collection, delete workspace, swap environment scope) is reversible from the Activity drawer or via Ctrl+Z. Hints carry a permanent dismiss key that lands them in **Settings → Hints and warnings** so the user can restore one without restarting.

### Workspaces tree + per-plugin DisplayName (#192 / #167)

Sources, collections, recordings, and environments all live as nodes under the Workspaces tree on the left rail. Plugin Settings reads each plugin's `DisplayName` from its registration so the row labels match what the plugin author calls itself (no more raw assembly-name fallback).

### Recordings as portable artefacts — `.bwr` format + `bowire mock <recording.bwr>` (#210 / #211)

Recordings get a standalone, self-contained file format. A `.bwr` is a single JSON archive that carries every step of a captured session — protocol metadata, request/response pairs, content-addressed bodies, timing — without depending on a workspace it was authored in. Drop one on a colleague's machine and it loads cleanly even when their workspace pins a different plugin set.

`bowire recording validate <file.bwr>` lints a recording before you share it. `bowire mock <recording.bwr>` is the positional one-shot replay verb: point it at a `.bwr`, the mock server spins up and matches incoming requests against the recorded steps via `MockHandler.TryMatch`, dispatching the recorded response body verbatim. Unmatched requests return a structured 404. All seven protocols (REST / gRPC / GraphQL / WebSocket / SSE / MQTT / SignalR) round-trip through replay; the integration suite pins each one.

### AI side-panel — BYOK cloud + MCP-client reversal (#25 Phase 3 + 4)

AI providers ship as opt-in NuGet plugins through a new `IBowireAiProviderFactory` seam. The standalone CLI bundles every option out of the box; embedded hosts pay only for the providers they install — `Kuestenlogik.Bowire.Ai` core stays free of cloud-provider SDK weight.

- **`Kuestenlogik.Bowire.Ai.OpenAi`** — BYOK OpenAI + OpenRouter via `Microsoft.Extensions.AI.OpenAI`. OpenRouter rides the same SDK with its own base URL, so one package covers both.
- **`Kuestenlogik.Bowire.Ai.Anthropic`** — BYOK Claude via the community-maintained `Anthropic.SDK` whose `Messages` property is already an `IChatClient`.
- **`Kuestenlogik.Bowire.Ai.Mcp`** — MCP-client reversal: Bowire connects as an MCP client to a user-configured host (stdio command or http URL) and routes chat through the first tool whose name reads as a chat / completion / sampling gateway. Lazy-connect on first call, so Settings-UI hot-swap stays cheap.

The Settings → Assistant tab grows a six-option provider dropdown (Ollama / LM Studio / OpenAI / Anthropic / OpenRouter / MCP), a password-input API-key row with a leave-blank-keep-existing convention + an explicit `__bowire_clear__` sentinel, and a per-provider privacy banner that names exactly where prompts go ("Prompts go to api.openai.com — Küstenlogik never sees the key, prompts, or responses"). API keys are never echoed back over the wire; the status endpoint surfaces a `hasApiKey` boolean instead.

### Benchmarks as a first-class rail — envelope architecture + 3 shapes (#131)

Benchmarks are their own rail-mode peer with Discover / Mocks / Flows / Recordings / Collections, no longer a buried inline expansion on a single request pane. The shipped surface is the **envelope architecture** — a saved Benchmark is a `{targets[], phases[], mode}` bundle rather than a one-method-one-config row — with three production-relevant target shapes wired:

- **single** — one unary call against a service + method
- **collection** — replay every item of a saved collection
- **recording** — replay every step of a saved recording

Phases follow the Artillery / k6 stages model (`duration` + `arrivalRate` | `vus`) so import / export round-trips cleanly to those formats:

- **Round-trip exports:** native Bowire envelope JSON · Artillery JSON · k6 script (a complete `.js` with stages + per-target HTTP calls)
- **Imports:** Artillery JSON + Postman Collection
- `{{var}}` ↔ `${var}` auto-rewrite at the import / export boundary so variables stay legal in both directions

Run output covers latency percentiles (p50 / p95 / p99), throughput (rps), and a status histogram (success / 4xx / 5xx / timeout / network). Saved runs persist in the active workspace + carry their spec so you can re-run them after switching workspaces — the sidebar shows a p95 meta on each saved row + live `N/total` while a run is in flight. Per-request "Benchmark this method" affordance on method-header + tree-row drops the current method into an existing envelope or spawns a new one seeded with it.

The remaining `random` + `scheduled` shapes, previous-run diff banner, and CSV / k6-summary / OTLP result exports ship in v2.1+ under their own tickets — #231 (random), #232 (scheduled + cron), #233 (diff banner), #234 (CSV / k6-summary / OTLP). #131 closes with the envelope architecture this release.

### Recordings as GB-scale artefacts — chunked storage Phase 1 (#144 → closeout #220)

Recordings move off the legacy single-file shape onto a chunked disk layout: one JSON file per step under `~/.bowire/recordings/<id>/`, with a manifest at `<id>.json` carrying the step ids + ordering. Request and response bodies extract into `bodies/<sha256>.bin` so duplicate payloads dedupe across steps + recordings — typical recordings of long-poll / SSE / WebSocket traffic shrink dramatically. The runtime reads the chunked layout transparently; old recordings load via the legacy reader and get re-saved into the chunked shape on the next mutation. Every `entityKind` / `id` / `responseRef` / step-file path passes through `SanitiseId` / `SanitiseHash` / `SanitiseStepFile` barriers that reject traversal / non-hex / out-of-bucket inputs — CodeQL `cs/path-injection` is pinned to zero alerts at the v2.0 cut. Filesystem watch + reconcile UI + lazy step-load on UI scroll continue under #144 in v2.1.

### Per-mode saved configurations — Presets framework (#140 Phase 1 → closeout #221)

A generic presets API replaces the per-mode hand-rolled "save current config" patches: `loadPresetsForMode` / `savePresetForMode` / `setDefaultPreset` / `deletePreset`, per-workspace storage keyed under `bowire_presets_<mode>` so presets ride the `.bww` export, a uniform Manage-Presets modal across modes, and a reusable `renderPresetsBar(mode, …)` top-of-pane bar with picker + "Save current as preset…" + "Set default" + "Manage…". Benchmarks and the Discover request-pane integrate first — Discover's method header carries a presets dropdown with per-method save / apply / set-default / add-to-collection and a single Save-as-preset action that mirrors the workspace dropdown pattern. Mocks / Parallel / Security / Proxy / Catalogue integrations follow in v2.1 under #140.

### Parallel sessions from recordings + collections — local fan-out (#132 minimal → closeout #222)

Recordings and collections get a "Run in parallel sessions" toolbar action that fans N copies of the run locally. Live state tracks per-session progress (started / active / completed counts, errors) and result aggregation reports per-session pass/fail + the overall outcome ("12 / 15 sessions completed, 3 errors at step 4"). No standalone rail mode — results land inline under the source that started the run so an operator chasing parallelism doesn't need to learn a new navigation step. Distributed fan-out across workers stays under #132 in v2.1.

### UI polish across the rc series

Through the v2.0 rc cycle every rail in the new shell got a uniform polish pass so they read as part of the same family — not nine slightly-different layouts. The biggest wins:

- **Recording detail pane** — shared `.bowire-pane-header` chrome (heading + rename pencil + danger trash via `bowirePrompt`); the flat 10-button toolbar regroups into Run / Build / Export gangs, Export becomes a split-button ▾ menu (HAR / HTML report / JSON with one-line hints).
- **Discover sidebar consolidation** — favorites toggle moves into the filter popup as its first option; the filter button moves up into the unified toolbar row next to `+ New`. One filter control instead of two related-but-separate ones.
- **Flows + Proxy sidebars** stop falling through to the legacy Discover services-tree path — each gets its own minimal sidebar renderer.
- **Home rhythm + drawer** — Continue / Start / Sections / Footer fit a 1080 px viewport without scrolling; Favorites / Recent titles open a right-side `renderDrawer` overlay with the full list (recent activity tiles carry relative timestamps).
- **Preset picker in the Discover request-pane header** — per-method save / apply / set-default / add-to-collection; default-star sits right in the tools cluster (analog of the workspace ✓ marker); `+ Save as preset…` action row at the bottom of the dropdown.
- **Security rail** drops its wrapper "Security" heading (no other rail puts a rail-name heading above its own content); Threat Model empty state swaps the bare ⚠ no-endpoints line for the shared empty-card chrome with "Open Discover" + "Add a source" CTAs.
- **Workspace settings tabs** reorder Variables → Secrets → Auth (Variables + Secrets are KV-shape, Auth is a different mental model).
- **Root-cause fix** — `code-export.js` was calling `syncFormToJson` from inside the render path, clobbering `formValues` + `requestMessages` with stale pre-merge DOM values every frame and breaking preset apply / history replay / Repeat-last-call. The fix swaps the mutation for a read-only `collectFormValuesFromState` snapshot.

## Breaking changes

Each change has been on a back-compat ramp through v1.9.x and is removed in 2.0.

### Wire format: `application/problem+json` drops the `{ error }` shim (#88 follow-up)

Every Bowire endpoint that returns a problem+json body has emitted both the RFC 7807 `title` + a legacy `error` field set to the same string. v2.0 drops the `error` field on the wire. Clients reading the response body should switch to `body.title` (RFC 7807) / `body.detail`. The shim is gone server-side; the JS workbench bundled with the CLI was updated in lock-step. OAuth callbacks keep their `error` field — that's RFC 6749 from the IdP, unrelated.

### `Microsoft.OpenApi` is no longer a transitive of `Kuestenlogik.Bowire.Protocol.Rest`

Hosts that referenced REST + called the discovery helpers directly must now also reference one of `Kuestenlogik.Bowire.Protocol.Rest.OpenApi2` or `.OpenApi3`. The standalone CLI bundles OpenApi2 (matches the .NET 10 ASP.NET ecosystem); embedded hosts pick whichever matches the rest of their stack.

### `Kuestenlogik.Bowire.Extension.MapLibre` → `Kuestenlogik.Bowire.Map`

Old package will be deprecated + unlisted on nuget.org after this release (#197). Swap the `<PackageReference>` to the new name; no API surface change.

### OpenTelemetry moved into `Kuestenlogik.Bowire.Telemetry`

Embedded hosts that want self-observability add a `<PackageReference Include="Kuestenlogik.Bowire.Telemetry" />`. Standalone CLI users see no behaviour change.

### `localStorage` cosmetic-state reset on major bump

Persistent data (workspaces, environments, recordings, collections, history, favorites) and persistent user choices (theme, watch interval) are preserved across the v1.x → v2.0 upgrade. Cosmetic UI state (active rail mode, open drawer, expanded services, filter chips, split mode) is reset to v2.0 defaults on first boot so the new shell isn't fighting a stale layout. A toast announces the reset; data is untouched.

### Workbench CSS surface

Several `bowire-ai-*` and helper classes were renamed or removed as part of the shell refactor (`.bowire-ai-empty` → `.bowire-pane-empty`, legacy `.bowire-ai-drawer*` chrome classes dropped, dead-class audit removed 20 unused rules). External CSS that hard-targeted these internal classes will need to update; no JS / HTML API surface is affected.

## Migration guides

### `${name}` → `{{name}}` variable syntax (#145 Phase 1 — soft deprecation)

Bowire has two interpolation syntaxes that resolve identically: the original Bash-style `${name}` (escape: `$${name}`) and the Postman / Mustache `{{name}}` (escape: `{{{{name}}}}`). v2.0 starts the planned migration window — **both syntaxes still work**, but `${name}` is now flagged as legacy and the canonical form going forward is `{{name}}`.

**What changes in v2.0:**
- A one-time per-workspace toast fires on workbench load when the active workspace's stored data contains `${...}` placeholders. The toast is snoozed via localStorage so the operator isn't nagged on every reload.
- The workspace-scope scanner walks recordings, collections, freeform requests, flows, and environments using the regex `/(?<!\$)\${[^}]+}/` — correctly skips escaped `$${...}`.
- New surfaces (Cmd+K palette, AI prompts, empty-state copy) only emit `{{...}}`.
- Documentation explicitly marks `${...}` as legacy.
- **`substituteVars()` continues to handle both syntaxes in parallel** — no existing recording / collection / saved request breaks.

**What you need to do for v2.0:**
- Nothing forced. Existing workspaces keep working with both syntaxes.
- When the toast fires, you can either dismiss it (the legacy syntax will keep resolving forever in v2.0) or open Settings → Hints and warnings to permanently dismiss it.
- For new content authored in v2.0, prefer `{{name}}` — and `{{env.NAME}}` / `{{prev.field}}` / `{{step1.field}}` / `{{runtime.now}}` / `{{secret.NAME}}` / `{{ai.NAME}}` for the source-prefixed forms that landed under #125.

**What's planned for v2.1 (#145 Phase 2):**
- A dedicated migration tool — Settings → Migration page or a one-shot CLI command — that walks every recording, collection, environment, flow, and freeform draft and rewrites `${name}` → `{{name}}` (and `${response.X}` → `{{prev.X}}`, `${now}` → `{{runtime.now}}`, &c.).
- Dry-run + diff view before commit. Per-workspace scope. Disk-stored recordings get touched too. Migration emits a single transition record so the workspace metadata says "migrated to `{{}}`-syntax at `<ts>`".
- Hard removal of the `${name}` parser is NOT planned for v2.1. The earliest deprecation cut would be v3.0, and only after the migration tool has been in place for a full minor.

## Acknowledgements

Closes 72 issues across the milestone — the full list is on the [v2.0 milestone page](https://github.com/Kuestenlogik/Bowire/milestone/8?closed=1). Special thanks to everyone who exercised the rc series and reported off-by-one drawer behaviour, single-tab vs multi-tab edge cases, and `.bowire-ai-*` class targeting in downstream CSS — every one of those reports landed as a concrete fix above.

---
## v1.9.0 — 2026-06-08 — AI for security

### Highlights — Tier 4 of the security roadmap lands

Bowire's product positioning has been "Burp Suite for the non-HTTP protocols, with schema-awareness, self-hosted, with AI-assisted threat modeling via MCP." v1.9 closes the AI-assisted half of that promise — every discovered API surface in the workbench can now be analyzed by an LLM that grounds itself in the actual schema + traffic, not generic web knowledge.

- **AI threat-model (#59)** — rank every discovered endpoint by attack-surface risk. The model reads the API's input/output shapes, auth posture, and recent traffic, and returns a sorted list with reasoning per endpoint. Sets the scan order for everything that follows.
- **AI Nuclei-template suggestion (#60)** — per endpoint, generate Nuclei templates targeting the identified risks. Bowire feeds the model the OpenAPI / proto / GraphQL schema; the model returns YAML that drops straight into the Nuclei runner.
- **AI findings triage (#61)** — every Nuclei hit gets a real-vs-false-positive verdict + a concrete fix suggestion. Cuts triage time per finding by an order of magnitude on a noisy scan.
- **AI schema-aware fuzz values (#62)** — boundary inputs per field, derived from the schema's types + constraints. Composes with the existing fuzz harness.
- **AI Settings UI (#63)** — pick the provider (Ollama, LM Studio, BYOK cloud) + endpoint + model directly from the workbench's Settings dialog. No `appsettings.json` edits, no restart.

The roadmap calls this **Tier 4**. Tiers 1–3 (record-as-attack, fuzz UI, MITM proxy) shipped through v1.4.x; Tier 5 (auto-exploit / proof-of-vulnerability) is its own framing and a different risk posture, deliberately out of scope here.

### Also in this release

The 94 commits since v1.8.0 carry a lot more than the marquee. Highlights of the supporting work:

**Security as a first-class drawer, with a tier that doesn't need AI (#111 + #112)**
- **Separate Security drawer.** Threat-Model + Nuclei template suggestion left the AI drawer for their own surface (shield icon in the topbar, `Ctrl/Cmd+Shift+S`). The AI drawer stays focused on conversational assistance (hints + chat). Mental model: AI = assistant, Security = scanner / analysis.
- **Heuristic ranking tier — no AI required.** Default ranking is now a deterministic rule engine (`ThreatHeuristic` in core): verb-based mutation scoring, BOLA-pattern detection on path params, admin / auth / PII path matching, anonymous-auth bumps, sensitive-field-name detection in the input shape. Sub-millisecond per endpoint. Each ranked row carries a `ruleTrace` so users can audit which rules fired against which endpoint.
- **AI tier becomes opt-in.** A tier toggle in the drawer lets the operator switch to AI-assisted ranking when they want semantic adjustments on top of the heuristic. Default heuristic → security tooling works on installs that don't have or don't want AI.

**AI assistant — full MCP-style tool calling (#89, #108, #109)**
- **Phase 1: chat grounding.** Every chat send prepends a workbench-state snapshot (loaded URLs + service names + selected method's full schema + recent calls) as a system prompt. The model answers from real data instead of generic web knowledge.
- **Phase 2: read-only tools (#108).** Three `Microsoft.Extensions.AI` `AIFunction`s available on every chat request: `bowire_list_services`, `bowire_describe_method`, `bowire_recent_history`. The model calls them mid-conversation to drill in. The Ollama path is wrapped in `FunctionInvokingChatClient` so the tool-call loop actually round-trips. Tool calls render as visible "Consulted X" steps in the chat transcript with collapsible args.
- **Phase 3: invoke tool (#109).** Opt-in via the "Allow AI to invoke methods" toggle in the drawer header. When OFF (default), `bowire_invoke` isn't even registered — the model literally cannot try. When ON, the AI dispatches through `protocol.InvokeAsync` (same path `/api/invoke` uses) and every call writes a JSONL row to `~/.bowire/.ai-actions.jsonl` for audit. Toggle is session-only — never persisted to localStorage.
- **Phase 4: UI navigation (#109).** `bowire_open_method` tool lets the AI navigate the workbench to the right method ("Show me `pet.findPetsByStatus`" → workbench opens the request pane on it). Always available, no side effects beyond UI state.
- **"Thinking…" feedback in the drawer.** Local models with tool calling take 15-45 s per turn. The chat now shows a live `(N s)` counter with a pulsing accent dot + a Cancel button that aborts the fetch — no more wondering whether the request hung.

**Workbench UX**
- **AI side panel moves into a right-side drawer (#90)** with a topbar toggle + `Ctrl/Cmd+Shift+A` shortcut. Persists across method/service switches. Was a peer of Response/Logs/Code in the response-pane tab strip, which forced you to choose between seeing the AI and seeing the response.
- **Connection-state pill in the topbar (#93)**. Aggregate state for every configured discovery URL — green when all connected, amber when partial / connecting, red when any failed. Hover for a per-URL breakdown with service counts + the failure message.
- **Body sub-tabs (#85)**. GraphQL Body now splits Query / Variables / Selection-set into a sub-tab strip; REST / gRPC / JSON-RPC get protocol-aware Form / JSON pair labels (gRPC: Message / JSON; JSON-RPC: Params / JSON; REST: Form / Body). Was a vertical stack of three surfaces; now one surface at a time.
- **Filter services by discovery URL** in the sidebar's filter popup. Multi-URL setups can narrow to one origin.
- **Theme toggle dropped its visible label** — tooltip still carries the state + next action; the topbar right cluster reads cleaner.

**API conventions**
- **RFC 7807 ProblemDetails (#88)** is the new shape for every API error response. `application/problem+json` with stable `type` URNs (`urn:bowire:ai:model-not-found`, `urn:bowire:discovery:no-match`, …), structured `detail`, and typed extensions per error class. Backward-compatible: every body still carries an `error` field set to the title so legacy readers degrade gracefully.
- **AI 404 errors now actionable (#87)**. Missing model surfaces as "Model 'X' isn't available — pull it with `ollama pull X` or pick a different model in Settings → AI", with a `links: [{rel: configure}]` extension pointing back at the settings page.

**Bug fixes**
- **#65** — URL input in the sidebar was read-only despite `lockServerUrl=false`. `el()` helper coerced `undefined` attribute values to the literal string `"undefined"`, which the browser treats as truthy.
- **#66** — Settings → Plugins tab was in a fetch/render infinite loop; the plugins-tab fetches now fire once per tab-open instead of once per render.
- **#81** — Standalone CLI without `--url` was falling back to embedded UI mode because the heuristic checked `serverUrls.length` instead of trusting `config.embeddedMode`.
- **#82** — Standalone CLI's discovery endpoint short-circuited to `[]` even when a runtime URL was passed via the sidebar.
- **#83** — Plugin discovery probes ran sequentially (12 plugins × ~2-3 s ≈ 30 s) and the frontend timed out at 12 s. Now parallel with `Task.WhenAll` + a 8 s per-probe ceiling.
- **#84** — Standalone discovery without a URL fell back to the workbench's own URL, which the JSON-RPC plugin matched with a phantom "Methods" stub.
- **#86** — gRPC/HTTP transcoding toggle rendered on every REST method because the predicate didn't check `method.source === 'grpc'`.

**Docs**
- New [Topbar UI Guide page](https://bowire.io/docs/ui-guide/topbar.html) documenting the brand + command palette + connection pill + env selector + AI drawer.
- Phase F (multi-tenant UI affordances), SCIM (Phase C), and the single-user → multi-tenant migration path now tracked as discrete issues for the Cruise-ship-out-of-preview path.

### Upgrading

- Pull the new tool: `dotnet tool update -g bowire`
- Or update an existing host: `dotnet add package Kuestenlogik.Bowire --version 1.9.0` + the matching protocol-plugin versions.
- Frontend / ProblemDetails: nothing to change. Legacy `body.error` reads still work; new `body.title` + `body.detail` + extensions are additive.
- AI defaults: if you don't have `Kuestenlogik.Bowire.Ai` installed, every AI feature stays gracefully off and the Phase-1 hint engine continues working — no extra config required.
- AI tool calling (#108 / #109): the chat side panel sends the workbench context with every request automatically. The `bowire_invoke` write-side tool is gated on the new drawer toggle ("Allow AI to invoke methods") — off by default; flip per session to let the AI dispatch real calls.

---
## v1.8.0 — 2026-06-06 — AI workbench

## Highlights

**AI side-panel (#25 — Phase 1 + Phase 2)**
The workbench learned to think. Phase 1 ships a deterministic hint engine that runs entirely without an LLM — fifteen rules that read live workbench state (selected method, last response, recordings, protocol, mock count) and surface contextual hints in a new side-panel slot. Phase 2 ships the optional [`Kuestenlogik.Bowire.Ai`](https://www.nuget.org/packages/Kuestenlogik.Bowire.Ai) NuGet: a `Microsoft.Extensions.AI` `IChatClient` seam backed by OllamaSharp, auto-detect for Ollama on `127.0.0.1:11434` + LM Studio on `:1234`, three endpoints (`GET /api/ai/probe-local`, `GET /api/ai/status`, `POST /api/ai/chat`), and CLI flags `--ai-provider` / `--ai-endpoint` / `--ai-model`. The standalone `bowire` CLI bundles the package; embedded hosts opt in by adding it. **AI Settings UI (#63)** lands the in-workbench picker for provider / endpoint / model — switching providers no longer needs a restart, persistence rides on `IBowireUserStore`, and embedded hosts that supply their own `IChatClient` win cleanly with a "host-managed" status badge. **Findings triage (#61)** adds a `?` button next to every Vulnerable row in the fuzz panel that asks the model "is this real, and how do I fix it?" — colour-coded confidence score, suggested fix, in-memory cache. Outbound calls stay opt-in (probe only touches loopback, chat only goes where you point it).

**Workbench mocks (#56 + #57)**
Mocks finally have a UI. The new `MockRegistry` + `/api/mocks` surface lets the workbench start mocks from any recording with one click, list them in the sidebar, and stop them without dropping to a terminal. **#57** adds a per-mock request log: every inbound call lands in a bounded ring buffer that the workbench tails live, so you can see exactly which step matched which incoming request and which ones missed. Backed by an `IMockRequestObserver` SPI so external observers (metrics emitters, tracers) can plug in.

**Auth seam (#28 Phase A + #28 Phase B + #31 + #32)**
The first real multi-user foundation. **#31** adds the `IBowireAuthProvider` SPI so plugins can wire authentication schemes + a default authorization policy, and adds a `Configure(IApplicationBuilder)` hook + the `UseBowireAuth()` middleware so providers can mount callback paths / claims-transformation. **#32** ships the OIDC plugin's required-claim filter and session-token forwarding for downstream service calls. **#28 Phase A + B** introduce the `IBowireUserStore` seam and migrate every per-user store onto it (`EnvironmentStore`, `RecordingStore`, `CollectionStore`, `FlowStore`, `PluginManager`, `PluginUpdateCheckService`, schema-hints, the new AI config file) — single-user installs keep the legacy flat `~/.bowire/` layout, multi-tenant deployments swap in a per-identity resolver once SCIM lands.

**Self-telemetry (#29 — Phase 1 + Phase 2)**
Bowire can now observe itself. Phase 1 ships the `BowireTelemetry` seam — a canonical `ActivitySource` + `Meter` on `Kuestenlogik.Bowire` with pre-declared instruments (`bowire.invoke.count`, `bowire.invoke.duration`, `bowire.discover.count`, `bowire.plugin.load`, `bowire.mock.requests`) — plus an opt-in `AddBowireTelemetry()` that wires OpenTelemetry against them. Phase 2 plumbs the instruments through the discovery loop (per-protocol probe counts + outcomes), the plugin scan (loaded vs disabled), and the mock pipeline (per-request counter with method / outcome / status tags). Operators opt in via `--telemetry` / `Bowire:Telemetry:Enabled=true`; OTLP endpoint / headers come from standard `OTEL_EXPORTER_OTLP_*` env vars. **Bowire.Samples** ships a Grafana 11 dashboard (`Bowire.Samples/dashboards/bowire-overview.json`) that reads non-zero panels from day one.

**Collections (#30)**
Postman-style named groups of saved requests, sequenced against the active environment. Backend persistence routes through the new `CollectionStore` (single-user installs land at `~/.bowire/collections.json`, multi-tenant routes per identity via `IBowireUserStore`), and the new flow-to-collection export turns any saved flow into a runnable test suite.

**Plugin lifecycle in the workbench (#27)**
The plugins panel got an Inspect button + modal so you can see version, metadata, and recent activity for any installed plugin from the workbench instead of running `bowire plugin list` in a terminal. Pre-release support flows through the daily update check + the install path.

**Multi-repo release cascade**
The release pipeline now discovers sibling repos dynamically by querying the Kuestenlogik org for repos carrying the `bowire-cascade` GitHub topic — adding a new sibling to the cascade is now opt-in via that repo's own Settings → Topics, no PR against the main release workflow required. Cascade templates live in `.github/sibling-templates/`. Auto-PR + auto-merge use the org's rebase-only convention.

---

## Maintenance

- New `post-release-floor-bump.yml` workflow auto-bumps `Directory.Build.props` to `<just-released>-dev` after every successful release, opens a PR, no hand edit required.
- Coverlet runsettings extended with `GeneratedRegex` on `ExcludeByAttribute` so the source-generator-emitted Regex partials no longer drag the line-coverage denominator.
- 1209 new unit + integration tests added across the v1.8 cycle: solution-wide line coverage at 92.7 %, branch 79.2 %, method 95.4 % (up from 90.9 / 78.1 / 93.2 at v1.7.0). Notable jumps: CollectionStore, MockRequestLog, MockRegistry, EnvironmentStore, AsyncApi binding resolvers, BowireAiRuntime — all now ≥ 95 % line.
- CodeQL `cs/log-forging` alert closed (#40) — defensive CR/LF scrub before logging mock IDs / recording display names.
- Marketing site (bowire.io): sharper headings across 5 solution pages, fixed an invisible `<code>` in the dev-loop-debugging hero, fixed the protocol-search input stacking the tag prefix, refreshed stale "planned" marks on the MCP comparison.
- Roadmap-sync workflow consolidated onto `BOWIRE_DISPATCH_TOKEN`.

---

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.7.0...v1.8.0

---
## v1.7.0 — 2026-06-03 — new protocol plugins

## Highlights

**Four new protocol plugins**
- [`Kuestenlogik.Bowire.Protocol.Nats`](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.Nats) — pub/sub + req/reply (Phase 1) + JetStream + Services API (Phase 2). Sits on the official NATS.Net 2.x client.
- [`Kuestenlogik.Bowire.Protocol.JsonRpc`](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.JsonRpc) — JSON-RPC 2.0 over HTTP and WebSocket.
- [`Kuestenlogik.Bowire.Protocol.Pulsar`](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.Pulsar) — Apache Pulsar producer / consumer / reader.
- [`Kuestenlogik.Bowire.Protocol.Soap`](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.Soap) — WSDL discovery + envelope construction.

**gRPC: Connect Phase 2**
Server-streaming over `application/connect+proto` is in. Connect-Web clients now talk to a Connect-mode Bowire workbench for streaming methods, not just unary.

**Mock-as-stand-in**
Recordings now carry the source schema verbatim. `bowire mock --recording <file>` re-emits `/openapi.json` (REST) and `/asyncapi.yaml` (messaging) so peer Bowires pointed at the mock discover the *full* original contract, not just the replayed slice. `bowire export ... --recording <file>` annotates each operation with `x-bowire-coverage: { recorded, stepCount }` so consumers see the replay gap explicitly.

**`bowire export` CLI**
New `openapi` / `asyncapi` subcommands that materialise the discovered contract as a YAML / JSON file on disk — useful for diff-against-prod and contract-test pipelines, especially for messaging protocols where hosts don't render an `/asyncapi.yaml` themselves.

**AsyncAPI**
`AsyncApiDocumentBuilder` emits AsyncAPI 3.0 from any discovery result. `NatsBindingResolver` dispatches `bindings.nats` operations to the NATS wire plugin.

**Tool I/O refactor (Phases 1-3 complete)**
Every CLI command path now flows through the `BowireCliIo` type instead of direct `Console.Out` writes. Output is structured, testable, and pipe-friendly — `bowire scan --json | jq` works the way you'd expect on every subcommand.

**Bootcamp launched on bowire.io**
The [Bowire Bootcamp](https://bowire.io/bootcamp/) — six units plus a capstone — is wired into bowire.io. Two parallel setup tracks (CLI / Embedded), then shared lessons on recording / mocking / MCP / plugin authoring / CI. The capstone weaves the lot into a single end-to-end Harbor-Tour scenario.

---

## Maintenance

## What's Changed
* chore(deps): Bump the xunit group with 1 update by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/20
* chore(deps): Bump the maplibre group with 2 updates by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/21
* chore(deps): Bump NATS.Net from 2.8.0 to 2.8.1 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/24
* chore(deps): Bump Microsoft.Identity.Web from 3.13.1 to 4.10.0 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/23
* chore(deps): Bump DotPulsar from 3.5.0 to 5.3.1 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/22


**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.6.1...v1.7.0

---
## v1.6.1 — 2026-05-25 — OIDC auth + MCP resources/prompts

## Highlights

- **OIDC auth plugin.** New `IBowireAuthProvider` SPI plus an OIDC provider implementation (roadmap "Up next #1").
- **MCP resources + prompts.** Both halves of the MCP adapter now expose resources and prompts, with `BowireMcpResources` and `BowireMcpPrompts` implementations and matching coverage.
- **Opt-in update check.** Background plugin-update check with a sidebar badge — disabled by default, opt-in only.
- **Docs refresh.** New "Updating Bowire and its plugins" setup page; docs aligned with the v1.5/1.6 CLI split, mock-emit, and lifecycle UI; PDF cover-page polish.

## Notes

The plugin-update check is opt-in: outbound network calls remain off until you flip the setting on. Auth-provider plugins implement the new `IBowireAuthProvider` SPI; the bundled OIDC provider is the reference implementation.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.6.0...v1.6.1

---
## v1.6.0 — 2026-05-25 — plugin lifecycle in the workbench

## Highlights

- **Plugin lifecycle in the workbench.** New manage panel lists bundled and external plugins, with REST endpoints for update / uninstall / latest-version checks. Bundled plugins are shown with their lifecycle disabled.
- **`--prerelease` on plugin install/update.** Opt into RC plugin versions explicitly from the CLI.
- **Plugin compatibility matrix.** New docs page lists every shipped first-party plugin's tested Bowire range plus a SemVer contract for plugin authors.
- **Security fix.** Closed `cs/command-line-injection` (#39) in the plugin REST endpoints.
- **Downloads page consistency.** All tiles use a uniform three-line layout (title, identifier, description); NuGet tiles collapsed to two lines; AMQP mark redrawn.

## Notes

If you author a Bowire plugin, please review the new compatibility matrix and the SemVer contract — first-party plugins now publish a tested Bowire-version range, and lifecycle endpoints assume that contract.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.5.1...v1.6.0

---
## v1.5.1 — 2026-05-24 — restores bowire scan CLI discovery in Release builds

## v1.5.1

_See the auto-generated change list below._

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



## What's Changed
* chore(deps): Bump the grpc group with 1 update by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/16
* chore(deps): Bump the xunit group with 1 update by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/17
* chore(deps): Bump SocketIOClient from 4.0.3 to 4.0.4 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/18
* chore(deps): Bump YamlDotNet from 16.3.0 to 18.0.0 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/19
* chore(actions): bump dependabot/fetch-metadata from 2 to 3 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/12
* chore(actions): bump docker/login-action from 3 to 4 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/14
* chore(actions): bump actions/upload-pages-artifact from 4 to 5 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/15
* chore(actions): bump codecov/codecov-action from 5 to 6 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/13
* chore(actions): bump actions/setup-node from 5 to 6 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/11


**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.5.0...v1.5.1

---
## v1.5.0 — 2026-05-20 — AsyncAPI discovery source + Nuclei template runner

## Highlights

- **AsyncAPI discovery.** Maps AsyncAPI 2.x and 3.x documents, with binding-detail extraction (QoS, retain, channel publish/subscribe routing), per-message overloads for V3, MQTT binding resolver, and a YAML pre-normaliser for unquoted enum scalars. Reads HTTP-served docs as raw bytes with explicit UTF-8 decoding.
- **`bowire scan --nuclei`.** Nuclei template runner — variable substitution, matcher → AttackPredicate translation, multi-path / payload matrices, end-to-end `--nuclei <dir>` flag. `--corpus` renamed to `--templates`.
- **Security scanner extracted.** `bowire scan` lives in its own project; CLI commands are now discovered via the `IBowireCliCommand` plugin SPI.
- **Launch UX overhaul.** Marketing-site quickstart split into role-axis (workflow) vs topology-axis (vessel), protocol-hint dropdown, MCP add-on toggle, container card refactor, photographed boat cards, and an in-tree masters set under `images/launch/`.
- **2026 visual refresh.** Token pass + glass topbar + sidebar polish across the workbench UI.

## Notes

`--corpus` has been renamed to `--templates` on `bowire scan` — update any existing scripts. The security scanner is now its own assembly and CLI commands are loaded through the new `IBowireCliCommand` SPI, so third-party CLI extensions are possible.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.4...v1.5.0

---
## v1.4.4 — 2026-05-17 — bowire scan exits 0 on findings, fail only on tool error

## v1.4.4

_See the auto-generated change list below._

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



## What's Changed
* chore(actions): Bump github/codeql-action from 3 to 4 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/6
* chore(actions): Bump actions/setup-dotnet from 4 to 5 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/3
* chore(actions): Bump actions/upload-artifact from 4 to 7 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/2


**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.3...v1.4.4

---
## v1.4.3 — 2026-05-17 — SARIF physicalLocation fix for Code Scanning upload

## Highlights

- **Scan SARIF physical location.** `bowire scan` SARIF output now includes a physical location alongside the logical one, so GitHub code scanning and other SARIF consumers attach findings to a file.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.2...v1.4.3

---
## v1.4.2 — 2026-05-17 — DAST-shaped SARIF + bundled NuGet ZIP asset

## v1.4.2

_See the auto-generated change list below._

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



## What's Changed
* chore(deps): Bump Microsoft.SourceLink.GitHub from 10.0.203 to 10.0.300 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/8
* chore(deps): Bump NuGet.Protocol from 7.3.1 to 7.6.0 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/9
* chore(deps): Bump System.CommandLine from 2.0.7 to 2.0.8 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/10


**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.1...v1.4.2

---
## v1.4.1 — 2026-05-17 — SARIF security-severity as numeric string

## v1.4.1

_See the auto-generated change list below._

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



## What's Changed
* chore(npm): bump @playwright/test from 1.59.1 to 1.60.0 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/5
* chore(actions): bump actions/download-artifact from 4 to 8 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/1
* chore(actions): bump softprops/action-gh-release from 2 to 3 by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/4
* chore(deps): Bump the aspnetcore group with 3 updates by @dependabot[bot] in https://github.com/Kuestenlogik/Bowire/pull/7


**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.0...v1.4.1

---
## v1.4.0 — 2026-05-17 — security-testing Tier 1–3 + flow-editor enhancements

## Highlights

Promotes v1.4.0-rc.1 to stable with no further changes.

- **Security testing tiers 1-3.** Recording-as-attack-replay + `bowire scan`, schema-aware fuzzing, plain-HTTP and HTTPS-MITM proxy with captured-flow store and workbench Proxy tab.
- **Flow editor.** Recording → Flow conversion, schema-aware Service/Method picker, Variable-Watch panel + `${var}` autocomplete, Foreach loops, inline assertions on Request nodes.
- **Map widget.** MIL-2525C tactical symbols from frame SIDC, ESRI satellite basemap option, maximize button, allowlisted offline basemaps, configurable default basemap via `Bowire:MapBasemap`.
- **Plugin loader hardening.** AssemblyDependencyResolver + pre-load contract check + structured PluginLoadResult, surfaced via `/api/plugins/health` and the Settings → Plugins tab.
- **UI + marketing refresh.** 2026 visual token pass with glass topbar + sidebar polish, deployment-modes section with animated vessel cards, license corrected to Apache 2.0.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.4.0-rc.1...v1.4.0

---
## v1.4.0-rc.1 — 2026-05-17 — preview of security-testing Tier 1–3

## Highlights

Preview of v1.4.0's security-testing tiers, flow editor, and map-widget upgrades.

- **Security testing tiers 1-3.** Recording-as-attack-replay + `bowire scan` subcommand (Tier 1), schema-aware + scope-aware fuzzing with workbench right-click integration (Tier 2), plain-HTTP and HTTPS-MITM proxy with on-the-fly leaf-cert minting, captured-flow store, and workbench Proxy tab (Tier 3). Plus a JWT toolkit, built-in passive TLS/banner/verbose-error checks, and a reusable GitHub Action.
- **Flow editor.** Recording → Flow conversion, schema-aware Service/Method picker, Variable-Watch panel + `${var}` autocomplete, Foreach loops, inline assertions on Request nodes.
- **Map widget upgrades.** MIL-2525C tactical symbols from frame SIDC, ESRI satellite basemap option, maximize button, allowlisted offline basemaps, configurable default basemap via `Bowire:MapBasemap`, mounting on unary responses too.
- **Plugin loader hardening.** AssemblyDependencyResolver + pre-load contract check + structured PluginLoadResult, surfaced via `/api/plugins/health` and the Settings → Plugins tab; fixed dual-loading of `Kuestenlogik.Bowire` into the plugin ALC.
- **UI + marketing refresh.** 2026 visual token pass with glass topbar + sidebar polish, animated deployment-modes section, license corrected to Apache 2.0 in five places, separated Solutions / Workflows / Features axes.

## Notes

Tier-3 HTTPS MITM mints leaf certs on the fly against a Bowire-owned CA — install it deliberately into the trust store you want to intercept. Captured proxy flows live in a workbench-managed store with explicit handoff into the recording pipeline.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.3.0...v1.4.0-rc.1

---
## v1.3.0 — 2026-05-12 — Frame-semantics framework (content-driven viewers)

## 1.3.0 — 2026-05-12

### Highlights

- **Frame-semantics framework — Bowire now mounts viewers and editors
  based on payload shape, not protocol.** Stream a `{ship, lat, lng,
  status}` message over any transport and a map tab appears alongside
  the streaming-frames pane the moment the first frame arrives; drop
  a PNG byte field into the response and an image viewer is the
  obvious next step. The detection is content-driven — Bowire's
  built-in heuristics match WGS84 coordinates, GeoJSON points, image
  magic bytes (PNG / JPEG / GIF / WebP / AVIF), audio magic bytes
  (WAV / Ogg / FLAC / MP3), and ISO-8601 / epoch timestamps against
  any payload that flows through `/api/invoke/stream`, regardless of
  whether the bytes arrived via gRPC, REST, GraphQL, SignalR, MQTT,
  WebSocket, Socket.IO, OData, MCP, or one of the sibling-plugin
  protocols. **Plugins ship transport-only — the framework does the
  rest.** The pgAdmin pattern: shape-of-data drives viewer choice,
  not protocol-author opt-in. See
  `docs/architecture/frame-semantics-framework.md` for the full
  architecture.

- **Auto-mounted map widget via the new
  `Kuestenlogik.Bowire.Extension.MapLibre` package.** Ships
  separately from Bowire core, so users who never invoke a
  coordinate-bearing method pay nothing for ~900 KB of MapLibre.
  Install with `dotnet add package Kuestenlogik.Bowire.Extension.MapLibre`;
  without it, the workbench surfaces a *"Install
  Kuestenlogik.Bowire.Extension.MapLibre to render coordinates on a
  map"* placeholder card next to the streaming-frames pane so the
  discovery path is self-documenting. The map uses MapLibre GL JS
  4.7.1 (BSD-3-Clause), vendored under the extension's embedded
  resources — **never a CDN reference, never an outbound HTTP fetch
  unless `Bowire:MapTileUrl` is explicitly configured** with a tile
  source the user picks. Offline-default style locked down at the
  source: no `glyphs`, no `sprite`, no labelled symbol layers, regex-
  over-bundle CI test pinning the absence so future tweaks can't
  silently re-introduce them.

- **Right-click semantic editing in the response tree.** Every leaf
  field carries a small `kind (source)` badge — `coordinate.latitude
  (auto)`, `image.bytes (plugin)`, `coordinate.ecef.x (user)`, …
  Right-click or click-on-badge opens a menu with three actions
  (Accept current / Reinterpret as / Suppress), three persistence
  tiers (session / user / project), and a scope picker
  (current discriminator only / all message types where path exists
  / all matching path names). Companion-field suggestions follow up
  every coordinate mark — if you tag `$.position.lat`, Bowire offers
  the most-likely sibling field as the longitude partner.

- **`bowire.schema-hints.json` persists annotations.** Three tiers,
  explicit escalation: session (in-memory, default), user
  (`~/.bowire/schema-hints.json`), project (`bowire.schema-hints.json`
  in the repo root, version-controlled, team-shared). Recording-step
  schema gains additive `discriminator` + `interpretations` fields —
  recordings made with 1.3.0 replay deterministically against any
  future detector heuristic drift; recordings captured under earlier
  Bowire versions still load unchanged.

- **Split-pane layout for paired streaming + viewer panes.**
  Coordinate-annotated methods default to a draggable horizontal
  split (streaming-frames list on the left, map on the right), per
  user preference persisted to `localStorage`. Multi-selecting frames
  in the streaming list (Ctrl/Shift-click) flies the map camera to
  the selection bounds.

- **New sample: `Bowire.Samples/SchemaSemantics`.** A deliberately
  plain-vanilla gRPC server that streams `{ ship, lat, lng, status }`
  frames at 1 Hz around Hamburg-Harbour. No `IBowireSchemaHints`, no
  Bowire-side companion, no annotations file — point Bowire at it,
  invoke `Ships/WatchShips`, watch the auto-detector mount the map
  widget by itself. The pgAdmin proof in a runnable form. Ships in
  the sibling `Bowire.Samples` repo, alongside the existing
  per-protocol samples.

### Behind the scenes

- The framework shipped in five phases plus a pre-release refactor:
  Phase 1 (annotation data model + layered persistence + resolution
  priority), Phase 2 (built-in detectors + sample-frame probe),
  Phase 3 (extension framework + map widget), Phase 3.1 (split-pane
  primitive + selection sync), Phase 3.2 (widget `selectionMode`
  capability), Phase 4 (right-click override UI), Phase 5
  (recording-side persistence + replay determinism), Phase 3-R
  (extract MapLibre into its own NuGet package + offline lockdown).
  Each phase landed independently testable; the integration story is
  in the ADR.
- Frame-prober runs once per `(service, method, message-type)` tuple
  exactly — the auto-detector pre-populates an `InMemoryAnnotationLayer`
  at `Auto` priority; on-the-fly resolution never re-scans the live
  frame. Performance characteristic is `O(sample-set)` once, not
  `O(frames)` ongoing.
- Annotation resolver enforces `User > Plugin > Auto` priority with
  `SemanticTag.None` acting as explicit suppression — the same one
  mechanism handles "this is a coordinate" and "no it's not".
- 2071 tests cover the new code paths; the v1.2.0 baseline was
  1923 tests.

### Live-smoke fixes shipped in the 1.3.0 release window

End-to-end smoke against the SchemaSemantics sample with the
MapLibre extension installed surfaced four bugs the unit + integration
suites couldn't have caught — all fixed before the release tag:

- `bounds.isFinite()` is a Mapbox-GL-JS API that doesn't exist on
  MapLibre's `LngLatBounds` — the guard `!bounds.isFinite` silently
  early-returned every `maybeFit()` call, so the map stayed centered
  at `(0, 0)` zoom 1 regardless of how many pins landed.
- `mountWidgetsForMethod` was only invoked when an extension was
  registered for the active kind, so the *placeholder-card path*
  that Phase 3-R landed never got a chance to fire when the user
  hadn't installed the extension yet.
- Extracted `widgets/map.js` referenced `config.prefix` from the
  core IIFE's closure scope — that scope doesn't exist in an
  external extension bundle. Fixed by deriving the base URL from
  `document.currentScript.src`.
- Streaming-frame detail pane used `highlightJson()` (no
  data-json-path anchors) instead of `renderJsonTree()` (which the
  Phase 4 semantics decorator needs), and even after the switch the
  decorator's path lookup was off by a `$.` prefix between JSONPath
  convention (annotations) and chain-variable convention
  (DOM data attrs). Both fixed.

### Migration

- **None breaking.** Existing recordings replay unchanged. Existing
  CLI invocations work. The framework is opt-in by content — methods
  that don't carry conventional coordinate / image / audio / timestamp
  field shapes see no UI change.
- The map widget IS opt-in by package: `dotnet add package
  Kuestenlogik.Bowire.Extension.MapLibre` to mount it. Without that
  package, coordinate-annotated methods surface a placeholder card
  with the install hint rather than a map.
- Recording-file format version remains 2 (set in 1.1.0). The new
  `discriminator` + `interpretations` step fields are additive and
  optional — readers tolerate their absence.
- Bowire.Samples floats `Kuestenlogik.Bowire 1.2.*` today. Bump to
  `1.3.*` after 1.3.0 indexes on nuget.org if a sample wants the
  frame-semantics framework, otherwise leave alone — none of the
  protocol samples need it for what they currently demonstrate.

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.2.0...v1.3.0

---
## v1.3.0-rc.1 — 2026-05-12 — preview of the frame-semantics framework

## Highlights

Preview of v1.3.0's frame-semantics framework, MapLibre extension, and recording determinism.

- **Frame-semantics framework.** Phase 1 data model + storage + resolver, Phase 2 built-in detectors + frame prober, Phase 3 MapLibre map viewer + split-pane layout, Phase 4 manual override UI + companion-field suggestion, Phase 5 recording interpretations + replay determinism.
- **MapLibre extension package.** Map widget extracted into its own NuGet package, with offline-mode glyph/sprite egress locked down.
- **Workbench polish.** Fixed streaming-toolbar wrap, placeholder cards without registered extensions, mid-streaming badges on the stream-detail tree, WGS84 detector accepting "lon" as a longitude alias.

## Notes

The MapLibre map widget now ships as a separate NuGet package and is loaded as an extension. Recording replay is now deterministic across the resolved frame-semantics interpretations.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.2.0...v1.3.0-rc.1

---
## v1.2.0 — 2026-05-11 — gRPC-Web transport in the gRPC plugin

## 1.2.0 — 2026-05-11

### Highlights

- **gRPC-Web transport in the gRPC plugin.** Opt-in via the URL hint
  `grpcweb@<server>` or the metadata header
  `X-Bowire-Grpc-Transport: web`. The default stays native HTTP/2,
  so existing callers are unaffected. Useful for services that ship
  gRPC-Web alongside native gRPC (e.g. Rheinmetall TacticalAPI on
  4267/4268) and for browser-fronted backends behind an HTTP/1.1
  ingress. Server-streaming + unary work fully; client-streaming
  and duplex stay native-only — the HTTP/1.1 trailer + framing
  constraints in `GrpcWebMode.GrpcWeb` don't carry them cleanly.
- **New sibling plugin: `Kuestenlogik.Bowire.Protocol.TacticalApi`
  (v0.1.0, preview).** Wraps Rheinmetall's TacticalAPI for
  situational-awareness systems. Build-time fetch of the upstream
  `.proto` files from a pinned commit, compile via `Grpc.Tools`,
  ship only the generated bindings — the EPL-2.0 `.proto` source
  never enters Bowire's Apache-2.0 tree. Install via
  `bowire plugin install Kuestenlogik.Bowire.Protocol.TacticalApi`
  and target with `bowire --url tacticalapi@<server>`. v0.1.0 covers
  descriptor discovery and the sidebar projection; typed CRUD +
  server-streaming pump come in v0.2.0. Ships from a sibling repo
  with its own release cadence — see
  <https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi>.
- **URL-hint surface extended.** The existing `<plugin>@<url>` syntax
  now supports transport-variant hints alongside plugin pins.
  `grpcweb@` is the first such hint; the extension point lives in
  `BowireEndpointHelpers.ResolveHint(hint) → (PluginId, Metadata?)`
  so future transports (e.g. WebTransport / HTTP/3 variants) can
  plug in the same way.
- **Site, DocFX docs, and social-media banner refreshed.** Marketing
  site lists the new TacticalAPI plugin (with a `preview` chip) and
  the gRPC card now mentions gRPC-Web. Docs site gains a dedicated
  TacticalAPI protocol guide and a `gRPC-Web transport` section in
  the gRPC guide. Open Graph card (`og-image.png`) regenerated via
  a new reproducible Playwright pipeline; the Storm→Surgewave rename
  finally reaches the social preview too.

### Behind the scenes

- `GrpcChannelBuilder.cs` consolidates the previous three
  `GrpcChannel.ForAddress(...)` call sites into one helper that
  picks native or web based on a single `GrpcTransportMode`.
  Discovery, invoke, and channel-open all flow through it.
- mTLS composes with gRPC-Web: when both are active, the existing
  client-cert `SocketsHttpHandler` becomes the inner of the
  `GrpcWebHandler`.

### Migration

- **None for existing callers.** No URL changes, no metadata changes,
  no breaking API. Opt into gRPC-Web only when the target requires it.
- Bowire.Samples already floats `Kuestenlogik.Bowire 1.1.*` — no
  sample-side action needed for 1.2.0 (none of the samples actually
  exercise gRPC-Web today). Bump to `1.2.*` only when a sample is
  added that demonstrates the new transport.

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.1.0...v1.2.0

---
## v1.2.0-rc.1 — 2026-05-11 — preview of gRPC-Web transport

## Highlights

Preview of v1.2.0's gRPC-Web transport + TacticalAPI plugin.

- **gRPC-Web transport.** Opt-in alongside the native HTTP/2 path on `Protocol.Grpc`, with serialised integration tests against Kestrel.
- **TacticalAPI plugin (preview).** New sibling plugin entry surfaced in docs and on the marketing site.
- **Brand OG card.** Refreshed social-card banner with the real Bowire mark and Surgewave instead of Storm.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.1.0...v1.2.0-rc.1

---
## v1.1.0 — 2026-05-11 — workbench mounts at / (breaking)

## 1.1.0 — 2026-05-11

### ⚠ Breaking change for the standalone CLI

**The `bowire` tool now mounts the workbench at `/` instead of `/bowire`.**

If you double-click `bowire.exe` or run `bowire --url …` from a terminal,
your browser opens at `http://localhost:5080/` (was `…/5080/bowire`).
The optional MCP adapter moves from `/bowire/mcp` to `/mcp` for the same
reason. Update any bookmarks; AI-agent configs that pointed at the
`/bowire/mcp` adapter endpoint need to be re-pointed at `/mcp`.

**Embedded callers are not affected.** `app.MapBowire()` (no pattern arg)
keeps defaulting to `/bowire`; `app.MapBowire("/your/prefix")` keeps
mounting wherever you told it to. The route-pattern arg is still
authoritative. The standalone tool now passes `"/"` explicitly because
it has no host app sharing the route table.

### Highlights

- **Standalone workbench URL is now the site root.** No more `/bowire`
  hop; the auto-open browser URL and the startup banner both point at
  `http://localhost:5080/`. The MCP adapter (opt-in via
  `--enable-mcp-adapter`) moves alongside it to `/mcp`.
- **"gRPC failed to map discovery endpoints" warning gone** on standalone
  startup. `MapDiscoveryEndpoints(...)` now only runs in `BowireMode.Embedded`
  where Bowire is mounted inside a real gRPC / SignalR / Socket.IO host;
  in standalone the CLI is a client, there's nothing to reflect.
  `BrowserUiHost` also now calls `builder.Services.AddBowire()` so every
  plugin's DI prerequisites land in the container.
- **Coverage uplift to ~90% globally.** Five packages at 100% (Mcp,
  Protocol.Mcp, Protocol.OData, Protocol.SocketIo, Protocol.GraphQL); the
  rest of the protocol plugins all sit at 85% or higher. CLI tool jumped
  from 46% → 97% across two rounds.
- **MCP docs cover all four roles in one place.** Bowire as MCP client,
  Bowire's adapter wrapping discovered APIs, Bowire-as-MCP-server over
  HTTP (`AddBowireMcp` + `MapBowireMcp`), Bowire-as-MCP-server over stdio
  (`bowire mcp serve`). Claude Desktop config examples for both standalone
  and embedded mounts.
- **Internal codename `Storm` rename to `Surgewave` finished inside the
  main repo (Phase 3).** Storm was Küstenlogik's internal placeholder
  while the product was being built; Surgewave is the public name it
  ships under. Phase 1+2 already brought the sibling plugin and its SDK
  in line; this completes the rename inside the main workbench — docs,
  namespaces (`Protocol.Storm` → `…Surgewave`), JS detector, CSS
  classes, URL scheme (`storm://` → `surgewave://`), plugin slug,
  package id. Pure source-tree hygiene — no external users were ever
  exposed to "Storm".
- **OAuth proxy uses `IHttpClientFactory`** instead of `new HttpClient()`
  per request — handler pooling, clean test seam, and `Bowire:Trust
  LocalhostCert` opt-in now also applies to OAuth proxy calls against a
  local IdP with a self-signed cert.

### Dependency updates

- `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 1.2.0 → 1.3.0
- `coverlet.collector` 6.0.4 → 10.0.0 *(test-only)*
- Bundled `morphdom` (the workbench's only third-party JS lib, served
  locally because the workbench has a no-network guarantee) refreshed
  to **2.7.8** with a `/*! morphdom 2.7.8 — ... */` version header so
  the version is greppable in source and in the minified bundle.

### Migration

- **Bookmarks pointing at `http://localhost:5080/bowire`** → drop the
  `/bowire` suffix.
- **AI agent configs pointing at `http://localhost:5080/bowire/mcp`** →
  switch to `/mcp`.
- **Embedded callers using `app.MapBowire()`** → no change. Your prefix
  defaults to `/bowire` still, exactly as before. The tool's behaviour
  is the only thing that shifted.

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.12...v1.1.0

---
## v1.1.0-rc.4 — 2026-05-11 — preview of workbench-at-root

## Highlights

Preview of v1.1.0's RC-publishing pipeline.

- **RC packages on nuget.org.** RC tags now also publish to nuget.org alongside GitHub Packages.
- **RC packages on GitHub Packages.** Push RC nupkgs into the GitHub Packages feed.
- **DocFX xref links.** Use `xref:` links so DocFX resolves namespace pages cleanly.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.1.0-rc.3...v1.1.0-rc.4

---
## v1.1.0-rc.3 — 2026-05-11 — preview of workbench-at-root

## Highlights

Preview of v1.1.0's Storm-to-Surgewave rebrand, HAR import, and large coverage push.

- **Storm to Surgewave rebrand.** Renamed the in-tree Storm plugin, brand assets, and docs to Surgewave across the main repo, marketing site, and packaging.
- **Standalone workbench at "/".** Standalone mode now mounts the UI at the root path; the old "/bowire" prefix is gone.
- **HAR 1.2 import.** New `bowire import har` CLI command maps HAR captures into `.bwr` recordings.
- **Coverage push across the stack.** Tool, Mcp, REST, GraphQL, gRPC, MQTT, Socket.IO, Mock, and SignalR channel coverage raised — most plugins now land between 90% and 100%.
- **Marketing + docs polish.** New Why-Bowire deep dive, About-name section with pronunciation, contact form via web3forms, refreshed UI screenshots, footer + outline + hero tweaks.

## Notes

This RC begins the Storm → Surgewave rename. Existing `KL.Storm` package and asset references continue to work, but new builds reference `Kuestenlogik.Surgewave`.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.12...v1.1.0-rc.3

---
## v1.0.12 — 2026-05-06 — custom domain bowire.io + tag-driven release pipeline

## 1.0.12 — 2026-05-06

### Highlights

- **Custom domain `bowire.io`.** The marketing site, docs, and downloads now
  live at <https://bowire.io>. HTTPS is enforced, the certificate auto-renews
  via Let's Encrypt. The old `kuestenlogik.github.io/Bowire/` URLs continue
  to work via 301 redirect, but every reference inside Bowire (NuGet
  package URLs, MSI's Apps & Features URL, in-app About / landing-footer
  links) now points at the apex domain.
- **Full release pipeline back online.** Tag-driven publish from a single
  workflow now builds:
  - 14 NuGet packages (core, mock, mcp, every protocol plugin, the CLI tool)
  - 6 self-contained standalone bundles (Linux / Windows / macOS × x64 / arm64)
  - MSI installers (x64 + arm64), DEB + RPM packages (x64 + arm64)
  - Multi-arch OCI container to GHCR
  - DocFX HTML zip + a custom-rendered PDF docs snapshot
  - Auto-PR to `microsoft/winget-pkgs` for stable tags
- **Samples page.** New <https://bowire.io/samples.html> surfaces the eleven
  reference apps from `Kuestenlogik.Bowire.Samples` — one per protocol plus
  the Combined showcase that runs five protocols against the same
  `HarborStore`.
- **Marketing-site polish.** Real Storm / Apache Kafka marks on the
  downloads page, Akka.NET card added, native-installer links wired up to
  `releases/latest/download/`, copy-button layout fixed for long package
  names without widening the cards.

### Pipeline plumbing

The pipeline split into three jobs (Linux artefacts + container, Windows
MSI, GitHub Release) is the right shape for the WiX v5 Windows-only
constraint and lets the Linux + Windows builds run in parallel. A handful
of edge cases the old single-job pipeline never reproduced got ironed out
along the way:

- nfpm `contents.src` doesn't expand env vars on its own (only top-level
  fields), so the workflow now pre-runs `envsubst` against `nfpm.yaml`.
- `dotnet publish -t:PublishContainer -p:ContainerRuntimeIdentifiers="x;y"`
  needs both RIDs in the assets file before it iterates them — fix is an
  explicit multi-RID `dotnet restore` ahead of the container publish.
- DocFX's `pdf` command relies on a Playwright browser auto-install that
  races on CI. Replaced with a custom `scripts/site/build-docs-pdf.js` that
  walks the rendered HTML tree and merges per-page PDFs via `pdf-lib`.

### Test stability

- `OpenApiUploadStore` is a static singleton mutated by two test classes;
  xunit.v3 ran them in parallel and `Assert.Single` would race. Both
  classes are now in the same `[Collection]` so they serialise.

---

_The full commit list, contributors, and compare-URL diff are auto-generated below._



**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.11...v1.0.12

---
## v1.0.12-rc.3 — 2026-05-06 — preview of the bowire.io + release-pipeline cut

## Highlights

Preview of v1.0.12's release-pipeline overhaul and downloads-page polish.

- **Release pipeline rebuild.** Split into linux + windows-msi + release jobs, pinned WiX to v5, dynamic nfpm resolution, multi-RID pre-restore before container publish.
- **Winget + container publishing.** Auto-PR gated on stable tags only, container docs updated for GHCR + Docker Hub.
- **Downloads page polish.** Proper Storm/UDP/DIS marks, Akka card, wired native installers, full Kafka SVG, layout fixes.
- **bowire.io domain.** Marketing site switched to the bowire.io custom domain.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.11...v1.0.12-rc.3

---
## v1.0.11 — 2026-05-05 — Socket.IO namespace selection

## Highlights

- **Socket.IO namespace selection.** Workbench can now target a specific Socket.IO namespace instead of the default one.
- **Refreshed marketing screenshots.** Batch-refreshed both the protocol cards and the sibling-plugin cards against the new v1.0.10 layout.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.10...v1.0.11

---
## v1.0.10 — 2026-05-04 — method-detail header layout fix

## Highlights

- **Method-detail header layout fix.** Restores a clean header on the method-detail pane after recent UI changes broke the layout.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.9...v1.0.10

---
## v1.0.9 — 2026-05-04 — gRPC client factory + cert-trust unification

## Highlights

- **gRPC HttpClient factory.** Routes gRPC through the shared HttpClient factory so dev-cert trust is consistent with the other HTTP-based plugins.
- **Socket.IO payload extraction.** Pulls the event payload out of the v4 envelope for readable streaming output (promoted from rc.2).
- **Shared HttpClient factory.** REST, GraphQL, and streaming plugins all share dev-cert handling (promoted from rc.1).

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.8...v1.0.9

---
## v1.0.9-rc.2 — 2026-05-04 — preview of Socket.IO payload extraction

## Highlights

Preview of v1.0.9's Socket.IO improvements.

- **Socket.IO payload extraction.** Pulls the event payload out of the v4 envelope so the stream pane shows the actual message.
- **Dedicated Socket.IO screenshot.** Marketing-site protocol card now shows a Socket.IO capture instead of a generic shot.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.9-rc.1...v1.0.9-rc.2

---
## v1.0.9-rc.1 — 2026-05-04 — preview of shared HttpClient factory

## Highlights

Preview of v1.0.9's HTTP plumbing work.

- **Shared HttpClient factory.** Centralises dev-cert trust handling across the REST, GraphQL, and streaming plugins.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.8...v1.0.9-rc.1

---
## v1.0.8 — 2026-05-04 — readable JSON in stream pane

## Highlights

- **Readable JSON in the stream pane.** Streaming payloads now render pretty-printed instead of as a single-line blob.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.7...v1.0.8

---
## v1.0.7 — 2026-05-04 — GraphQL/SSE streaming fixes + protocol screenshots

## Highlights

- **GraphQL/SSE streaming fixes.** Stabilised GraphQL subscription and Server-Sent Events handling in the workbench.
- **Protocol screenshots.** Refreshed the protocol-card screenshots used across the marketing site.

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.6...v1.0.7

---
## v1.0.6 — 2026-05-04 — plugin hint URLs + --disable-plugin flag

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.5...v1.0.6

---
## v1.0.5 — 2026-05-04 — SignalR no-arg streaming + hub-URL resolution fix

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.4...v1.0.5

---
## v1.0.3 — 2026-05-04 — global localhost-cert trust + WebSocket plugin

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.2...v1.0.3

---
## v1.0.2 — 2026-05-04 — mobile-site polish + protocol-card screenshots

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.1...v1.0.2

---
## v1.0.1 — 2026-05-04 — discovery short-circuit on first run + UDP/Akka cards

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/compare/v1.0.0...v1.0.1

---
## v1.0.0 — 2026-05-03 — initial public release

**Full Changelog**: https://github.com/Kuestenlogik/Bowire/commits/v1.0.0

---

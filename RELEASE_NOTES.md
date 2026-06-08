# Bowire Release Notes

Hand-curated highlights per release. The full commit list is auto-generated
by GitHub on the release page; this file is the editorial layer above it.

Format per version: `## <version> — <date>` heading, free-form prose +
bullets summarising what shipped, why it matters, and any breaking changes.
The release workflow extracts the most-recent matching block at tag time
and uses it as the GitHub Release body.

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
- **Marketing-site polish.** Real Surgewave / Apache Kafka marks on the
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
  races on CI. Replaced with a custom `scripts/build-docs-pdf.js` that
  walks the rendered HTML tree and merges per-page PDFs via `pdf-lib`.

### Test stability

- `OpenApiUploadStore` is a static singleton mutated by two test classes;
  xunit.v3 ran them in parallel and `Assert.Single` would race. Both
  classes are now in the same `[Collection]` so they serialise.

---

## 1.0.11 — 2026-05-05

Socket.IO namespace selection (`X-Bowire-SocketIo-Namespace` header), plus
the rolling site/screenshot refresh from the 1.0.10 method-detail header
layout fix.

---

## 1.0.10 — 2026-05-05

Method-detail header layout fix in the workbench UI.

---

## Older releases

For 1.0.9 and earlier, see the auto-generated entries on
<https://github.com/Kuestenlogik/Bowire/releases>.

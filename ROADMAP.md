# Bowire Roadmap

A living list of what's *next*. For what's already shipped, see [GitHub Releases](https://github.com/Kuestenlogik/Bowire/releases) (the authoritative changelog) and the per-feature ADRs under [`docs/architecture/`](docs/architecture/). Older entries are pruned from this file once the corresponding release lands — history lives in git log + the release notes.

## In progress

### Security testing tool — remaining tiers

ADR: [`docs/architecture/security-testing.md`](docs/architecture/security-testing.md). The product positioning: *"Burp Suite for the non-HTTP protocols, with schema-awareness, self-hosted, with AI-assisted threat modeling via MCP."* Tier 1 (anchor recording-as-attack-replay, built-in passive checks, authentication profile, scope-awareness, vulnerable-by-design sample app), Tier 2 (workbench right-click fuzz UI, CLI fuzz subcommand, multi-protocol attack template library bootstrap, JWT toolkit), and Tier 3 (intercepting proxy Stages A/B/C — capture store, HTTPS MITM, workbench Proxy view) shipped through v1.4.x. Open items:

- [ ] **Tier 2 — Multi-protocol attack template library** in [`kuestenlogik/bowire-vulndb`](https://github.com/Kuestenlogik/Bowire.VulnDb). The bootstrap (7 templates, CI validation against the vulnerable-by-design sample) shipped; the next wave is community-contributable templates per protocol (gRPC reflection variants, GraphQL alias amplification, SignalR brute-force, OData IDOR, MQTT ACL bypass, WebSocket subprotocol confusion …). Monthly NVD-sync opens issues for new CVEs that lack templates. `bowire vulndb update` plumbing for sync.
- [ ] **Tier 4 — MCP-driven threat modeling**: Bowire's MCP adapter exposes a `bowire.threat-model` prompt → LLM looks at the discovered service tree and proposes top-N riskiest endpoints + suggested attack templates. Operator confirms → templates run. The LLM is the smart-default picker; Bowire is the execution engine.

### AI side-panel integration

ADR: [`docs/architecture/ai-integration.md`](docs/architecture/ai-integration.md). Concept phase — design constraints (no Bowire-hosted cloud, no vendor-key-on-marketing-site, no paid SKU; AI features must be a property of the user's environment) pinned before the side-panel lands. Three model-access modes (BYOK cloud, local Ollama/LM-Studio, MCP-client) plus a deterministic hint engine that works without any LLM. Targets v1.5+.

## Next

### Auth-provider extension SPI (Phase A — core seam)

Today's `bowire` standalone has no auth surface — every endpoint is open on whatever URL the user binds to. That's fine on a laptop, but the moment someone installs Bowire on a shared host the gate has to exist. Rather than ship OIDC (and `Microsoft.Identity.Web`'s transitive weight) in core, give Bowire a third extension type so identity providers slot in the same way protocols and UI widgets do.

- [ ] **`IBowireAuthProvider` SPI** — sibling to `IBowireProtocol` and `IBowireUiExtension`. Surface area: an `AddAuthentication(IServiceCollection, BowireAuthOptions)` hook that wires up the authentication scheme, and a `BuildDefaultPolicy(AuthorizationPolicyBuilder)` hook so endpoints can require the auth before serving. The provider declares an id (`"oidc"`, `"saml"`, `"apikey"`, …) that maps to a CLI flag.
- [ ] **`--auth-provider <id>` CLI flag** (default: none) plus provider-namespaced flags (`--auth-oidc-authority`, `--auth-oidc-client-id`, …). When unset, behaviour is identical to today. When set, the named provider must be discoverable in the plugin load path or `bowire` fails fast with a clear error.
- [ ] **Endpoint integration** — `BowireApiEndpoints.Map` reads the active auth provider once at startup and applies `.RequireAuthorization()` to every map-call. MCP adapter + UI shell follow the same gate. Embedded mode is unchanged: when the host has its own auth pipeline configured, the host's policy wins (Bowire's hook is opt-in only).
- [ ] **Plugin-load privilege** — auth providers need slightly broader access than protocol plugins (they touch `IApplicationBuilder`, not just `IBowireProtocol`). Either extend `BowirePluginLoadContext` with an `IsAuthProvider` capability flag, or load auth assemblies through a separate load-context.

### Auth: OIDC provider plugin (Phase A — first concrete impl)

The first concrete auth-provider plugin, riding the SPI above. Ships as `Kuestenlogik.Bowire.Auth.Oidc` (separate NuGet) so the heavy `Microsoft.Identity.Web` dependency only lands in installs that actually use OIDC — same MapLibre-style separation that kept ~870 KB of map assets out of the core distribution.

- [ ] **`Kuestenlogik.Bowire.Auth.Oidc` plugin** implementing `IBowireAuthProvider` with id `"oidc"`. Built on `Microsoft.Identity.Web` so Azure AD, Okta, Keycloak, and any OIDC-compliant IdP work without provider-specific code paths.
- [ ] **CLI surface** — `--auth-provider oidc --auth-oidc-authority https://login.example.com --auth-oidc-client-id <id> --auth-oidc-required-claim <claim>=<value>`. Required-claim filter is the access gate ("must be member of group X") since Bowire still has no native user/group model.
- [ ] **Single-tenant gate, not multi-tenant**: every authenticated caller sees the same `~/.bowire/`. Data separation needs Phase B — Phase A is "lock the door", not "give every user their own room".
- [ ] **Token forwarding to target services** — auth middleware exposes the access token via `HttpContext.User` so the request pane's Auth tab can pick "Use my session token" as a forwarding mode.

### Multi-tenant data model + SCIM (Phase B — blocked on Phase A)

Once Bowire knows who's calling, the next ceiling is "everyone shares one `~/.bowire/`". For real multi-user installs each authenticated identity needs its own slice of state — recordings, environments, collections, flows, plugin installs.

- [ ] **User-scoped storage** — replace `Path.Combine(homeDir, ".bowire", "environments.json")` with an `IBowireUserStore` that resolves to `~/.bowire-server/users/<sub>/environments.json` (or a real DB). Every consumer (`EnvironmentStore`, `RecordingStore`, `CollectionStore`, `FlowStore`, `PluginManager`) routes through the seam. Standalone single-user mode keeps the flat layout by binding the store to a synthetic "default" user.
- [ ] **SCIM 2.0 endpoints** — `/scim/v2/Users` + `/scim/v2/Groups` per RFC 7644. Compliance test suite to verify Okta + Azure AD's provisioning sync round-trips correctly.
- [ ] **Per-user plugin installs** — split `~/.bowire/plugins/` into a system-wide tier (admin-managed) plus a per-user overlay so users can install workflow-specific plugins without admin help.
- [ ] **Migration path** — single-user installs upgrading into multi-tenant need a one-shot migration that promotes the existing flat `~/.bowire/` into the calling user's slot.

### Collections (Postman-style test suites)

Complement the existing Recordings feature (auto-captured sessions) and the shipped Flows (visual sequence builder) with manually curated request collections.

- [ ] **Collections** — named groups of saved requests, independent of recordings. Each collection item stores: protocol, service, method, body, metadata, expected status — everything needed to re-execute the request standalone. Items can be added manually ("Save to collection" from the request pane), imported from a recording, or created from the freeform request builder.
- [ ] **Collection Runner** — execute all items in sequence against the active environment. Variable substitution runs per-item, so `${baseUrl}`, `${token}`, etc. resolve fresh. Response chaining between items via `${response.X}` carries values forward.
- [ ] **Per-environment execution** — run the same collection against Dev, Staging, Prod by switching the active env. Results stored per (collection, environment) pair so regressions are visible side-by-side.
- [ ] **Persistence + Postman import** — JSON files in `~/.bowire/collections/`, synced via the same disk-sync pattern as environments and recordings. Parse Postman Collection v2.1 JSON, map `{{variable}}` → `${variable}` automatically.
- [ ] **Flows: Export as test** — flatten a flow into a linear collection for CI execution (no visual editor needed in CI).

### Protocol plugins — next wave

**Tier 1 — high value, fits the model:**

- [ ] **MQTT** (`Kuestenlogik.Bowire.Protocol.Mqtt`) — MQTT 3.1.1 / 5.0 via MQTTnet. Topics map to services, publish/subscribe map to unary/streaming. Discovery scans `$SYS/#` or a configured topic prefix. Strong IoT differentiator.

**Tier 2 — useful, more niche:**

- [ ] **Connect (Buf) support in gRPC plugin** — gRPC-compatible RPC over HTTP/1.1+JSON. Not a separate plugin — extend `BowireGrpcProtocol` with a Connect wire-format mode flag. Highest impact-per-effort among the next-wave items.
- [ ] **Kafka** (`Kuestenlogik.Bowire.Protocol.Kafka`) — Apache Kafka via `Confluent.Kafka` + Schema Registry. Cluster ↔ server, topic ↔ method, produce ↔ unary, consume ↔ server-streaming.
- [ ] **AMQP** (`Kuestenlogik.Bowire.Protocol.Amqp`) — AMQP 0.9.1 + 1.0 via `RabbitMQ.Client` + `AMQPNetLite`. Discovery via Management HTTP APIs.
- [ ] **SOAP** (`Kuestenlogik.Bowire.Protocol.Soap`) — SOAP 1.1/1.2 via WSDL. Operations ↔ methods, port types ↔ services. Response pane needs an XML highlighter.
- [ ] **NATS** (`Kuestenlogik.Bowire.Protocol.Nats`) — NATS core + JetStream. Subjects ↔ methods.
- [ ] **Generic JSON-RPC browser** — generalise the existing MCP JSON-RPC client into a standalone protocol that can browse any JSON-RPC 2.0 endpoint.
- [ ] **DIS** (`Kuestenlogik.Bowire.Protocol.Dis`) — IEEE 1278 Distributed Interactive Simulation. UDP multicast listener for DIS PDUs. Standalone-mode plugin for simulation environments.
- [ ] **OTLP** (`Kuestenlogik.Bowire.Protocol.Otlp`) — OpenTelemetry Protocol listener. Bowire boots a receiver (gRPC `:4317` + HTTP `:4318`), instrumented apps push traces/metrics/logs at it. First passive-listener mode where Bowire is the server, not the client.
- [ ] **Surgewave** (`Kuestenlogik.Bowire.Protocol.Surgewave`) — Surgewave tap stream browser. Sibling repo + plugin scaffolding ready; **blocked on the `Kuestenlogik.Surgewave.Client` SDK going public**.

### AsyncAPI as a discovery source

AsyncAPI is the OpenAPI analogue for event-driven APIs — a schema spec that describes channels, operations, messages, and the transport bindings (MQTT, Kafka, AMQP, WebSocket, NATS, …) those channels use. Rather than ship "AsyncAPI" as its own wire-plugin (it has no wire — the wire is whatever the binding says), AsyncAPI lands as a *discovery source* that drives Bowire's existing transport plugins. The mental model matches `bowire --url ./openapi.yaml`: hand Bowire the schema, it builds the method list, calls go out over the right transport.

Built on the official AsyncAPI .NET SDK (`Neuroglia.AsyncApi.Core` + `.IO`, repo: [asyncapi/net-sdk](https://github.com/asyncapi/net-sdk)). The `Client.Bindings.*` packages are *not* used — they ship Invocation-side code (own MQTT / Kafka clients etc.) that would duplicate Bowire's existing wire plugins. We use Neuroglia for schema + reading only; wire calls keep going through Bowire's own protocol plugins (MQTT via `Kuestenlogik.Bowire.Protocol.Mqtt`, Kafka via the third-party sibling plugin, …) at runtime through `BowireProtocolRegistry`.

- [x] **AsyncAPI 3.0 loader** — parse `asyncapi.yaml` / `.json`, follow `$ref` resolution (local + remote), expand `components.messages` / `components.schemas`, surface `servers[]` as Bowire targets. Multi-server documents become multi-URL discoveries (the same `--url X --url Y` shape the workbench already accepts). *(Phase A2.)*
- [x] **Channel → method mapping** — each channel becomes a Bowire method node in the sidebar. Operations (`send` / `receive`) become the streaming direction. Channel parameters → method parameters. Message payloads → request/response bodies; multiple declared messages surface as method overloads. AsyncAPI tags `send`/`receive` from the application's perspective, Bowire is the test client — polarity inverts once in this mapping layer rather than per binding. *(Phase A3 — operations now produce one BowireMethodInfo per `send`/`receive` operation with the right streaming direction; per-message overloads remain a Phase A4 item.)*
- [ ] **Phase A — MQTT binding** — translate `bindings.mqtt` (topic, qos, retain) into the existing MQTT plugin's invocation contract. First end-to-end target: a published AsyncAPI doc → discovery → publish/subscribe against a live broker. *(A3 ships the resolver + routing; A4 adds binding-field extraction + broker integration test — see below.)*
- [ ] **Phase B — Kafka + WebSocket bindings** — extend to `bindings.kafka` (topic, key, schema-registry-ref) and `bindings.ws` (subprotocol, headers). Both plugins exist (Kafka third-party, WebSocket first-party); the work is the binding-translation layer.
- [ ] **Phase C — AMQP / NATS / SNS-SQS bindings** — gated on the underlying transport plugins landing (see the Tier-2 plugins above). The loader stays the same; each binding adds a translation case.
- [ ] **Marketing-site listing follows Phase A** — until at least one binding works end-to-end the protocol stays off the public `BOWIRE_PROTOCOLS` list. No "coming soon" tile.
- [ ] **AsyncAPI schema export** — inverse of the loader: emit an AsyncAPI 3.0 document from the discovered topics/methods of running MQTT/Kafka/WebSocket targets. Mirrors the planned OpenAPI export (see Planned section).

#### Phase A4 — fix the YAML-deserializer bug + finish MQTT binding

Phase A3's MQTT resolver works end-to-end *without* the document declaring a `bindings:` block. As soon as `bindings.mqtt.qos: 2` (or any unquoted version like `asyncapi: 3.0.0`) is present, the Neuroglia SDK reader throws — its `StringEnumDeserializer` asks YamlDotNet for a `Decimal`-typed scalar, the YAML implicit-type resolver already classified the value as int / string, `Decimal.Parse` blows up. Not a Bowire bug, not a Bowire/Neuroglia interface bug, not a Bowire-MQTT-plugin bug: a layer-below issue in `Neuroglia.Serialization.YamlDotNet`'s `StringEnumDeserializer`. Filed upstream as [asyncapi/net-sdk#76](https://github.com/asyncapi/net-sdk/issues/76) — full repro + stack trace live there.

- [x] **File the upstream issue** — landed as [asyncapi/net-sdk#76](https://github.com/asyncapi/net-sdk/issues/76).
- [ ] **YAML pre-normaliser for AsyncAPI docs** — until the SDK is patched, walk the document before the reader and quote any unquoted scalar that's about to land on an enum-typed property (`asyncapi`, `info.version`, `bindings.mqtt.qos`, …). One-file utility, parallel to (not a fork of) the SDK reader. Tested round-trip against the AsyncAPI 3 example corpus.
- [x] **Bindings-detail extraction** — `AsyncApiBindingsExtractor` walks the raw YAML via YamlDotNet's representation model (side-path around the Neuroglia SDK reader's `bindings.mqtt.qos` crash) and pulls every `operations.<opKey>.bindings.<id>.<field>` scalar into a per-operation map. Populated into `AsyncApiChannelContext.BindingFields` at invoke time; `MqttBindingResolver` reads `qos` + `retain` from there and translates the AsyncAPI integer-form (`0/1/2`) into the textual MQTT-plugin form (`AtMostOnce`/`AtLeastOnce`/`ExactlyOnce`). Caller-supplied metadata still wins so a UI can override the doc's qos for a one-off send. Nested binding fields (LastWill mappings etc.) are skipped today — typed accessors arrive when a resolver actually needs them.
- [x] **MQTT broker integration test** — `MqttBindingResolverIntegrationTests` spins up an in-process MQTTnet broker on an ephemeral port, hands the AsyncAPI plugin a doc that declares `bindings.mqtt.qos: 2 + retain: true`, invokes the send-operation through the full discovery → resolver → plugin chain, and asserts the broker delivered the message with the right topic + payload + qos to a subscriber. No Testcontainers / Docker dep — keeps the test runnable on any CI without extra setup. Lives in `Kuestenlogik.Bowire.AsyncApi.Tests` next to the unit suite.
- [x] **AsyncAPI 2.x mapping** — `MapV2Channels` walks `channels[].publish` + `channels[].subscribe` (V2's inline-operation shape) into the same `BowireServiceInfo` topology V3 produces. Channel.Subscribe → asyncapi-receive (we receive), Channel.Publish → asyncapi-send (we send). Operation-id used as method name when set, else "publish" / "subscribe" fallback.
- [x] **AsyncAPI 2.x invocation routing** — `InvokeV2Async` mirrors the V3 dispatch shape against V2's different lookup model: `service` is the channel key (which IS the address in V2), `method` matches the publish/subscribe slot's operationId (or the fixed fallback). Server selection + resolver routing reuse the V3 path. V2 binding-detail extraction is still a follow-up — for now the resolver gets an empty BindingFields map and falls back to its defaults.
- [ ] **Per-message overloads** — AsyncAPI operations can declare multiple `messages[]`. Phase A3 collapses them into one method per operation; A4 splits them into overload-style child methods so Bowire's request form can pick the right message schema.

### Polyglot plugins via sidecar bridge

Bowire plugins today are .NET assemblies implementing `IBowireProtocol`. That locks out teams whose best protocol library lives in Rust (Zenoh), Python (paho-mqtt + the whole IoT/ML stack), Go (NATS core, Temporal), Node.js, or C++. Rather than port every protocol library to .NET, run such plugins as **sidecar processes** and bridge them into the host via JSON-RPC over stdio — the same transport LSP, MCP, and DAP settled on.

- [ ] **Sidecar plugin contract** — JSON-RPC 2.0 over stdio that maps 1:1 onto `IBowireProtocol` (discover / invoke / invokeStream / openChannel / channel.send / channel.close / initialize / shutdown / ping).
- [ ] **Sidecar manifest** — `plugin.json` next to the executable declares `packageId`, `protocol`, `executable`, `args`, `envPrefix`.
- [ ] **`SidecarBowireProtocol` adapter** in the core — implements `IBowireProtocol` by translating every method call into a JSON-RPC request over stdio.
- [ ] **Per-language SDKs** — Python (`pip install bowire-plugin`), Node.js (`@bowire/plugin`), Go (`bowire/go/plugin`), Rust (`bowire-plugin` crate).
- [ ] **Lifecycle + safety** — sidecar crashes surface as protocol errors; auto-restart on exit (exponential backoff); per-call timeout; env-inheritance.
- [ ] **Packaging + install** — `bowire plugin install` second code path: fetch zipped release artifact (GitHub Releases / private feed / OCI registry), unpack into `~/.bowire/plugins/<id>/`. `bowire plugin list` shows `kind: nuget | sidecar`.
- [ ] **Template** — `dotnet new bowire-plugin --sidecar python` in the [Templates repo](https://github.com/Kuestenlogik/Bowire.Templates).

### Replay-Mock-Server — Phase 3 polish

Phase 1+2 (static + streaming + dynamic values + multi-protocol + chaos + stateful + schema-only + miss-capture) shipped through v1.x. Phase 3 polish items remaining:

- [ ] **DIS replay** — once the DIS plugin lands above, replay extends trivially.
- [ ] **HTTPS MITM / record mode** — WireMock-style transparent proxy that records real traffic. Deferred unless demand picks up — the existing recording-from-UI surface already covers most needs.

### Bowire.Mcp — remaining tools

The self-service MCP server (Bowire's own operations exposed as MCP tools so AI agents drive the workbench). Phase 1+2 shipped; Phase 3 streaming + mock control shipped. Remaining:

- [ ] **`bowire.assert(stepIndex, path, op, expected)`** — append a test assertion onto the active recording's step.
- [ ] **`bowire.har.import(path)`** — once HAR-import (below) lands, expose it as a tool so agents can ingest Playwright / DevTools captures.
- [ ] **`bowire.record.start/stop/replay`** — currently stubbed. Active-recording state needs to be lifted out of browser localStorage first.
- [ ] **MCP Resources** for read-only data: `bowire://recordings/<id>`, `bowire://environments/<name>`, `bowire://history`, `bowire://services/<protocol>`.
- [ ] **MCP Prompts** for canned AI workflows: `bowire.smoke-test(url)` (discover → invoke every unary → assert HTTP-200), `bowire.regression-hunt(urlA, urlB)` (run the same suite against two URLs and diff responses).
- [ ] **`--allow-invoke` mode** — widens the allowlist to all URLs the user has typed into Bowire at least once. Today's choice is binary (env-only vs arbitrary).
- [ ] **`mcp serve --attach <workbench-url>`** — leight-weight adapter mode: instead of running its own discovery / state, the MCP server forwards every tool call (`bowire.discover`, `bowire.invoke`, `bowire.record.start`, …) to the HTTP API of an already-running Bowire workbench. Closes the confusing case where the browser-side workbench and the MCP-side process see different live state because they ran independent discoveries. Auth: re-use the workbench's allowlist + env-only modes; the attach URL is local-only by default.
- [ ] **Dual-MCP endpoint inside the workbench process** — when `--enable-mcp-adapter` is on, expose Bowire's own ops (`bowire.discover`, `bowire.record.start`, …) on a second MCP endpoint (`/mcp/bowire`) alongside the existing target-methods endpoint (`/mcp`). Eliminates the two-process split entirely: one Bowire instance, one source of truth for live state, two MCP endpoints — adapter (target API) and serve (Bowire ops). Makes the standalone `bowire mcp serve` subcommand a niche tool for environments without a running workbench.
- [ ] **Confirmation pattern for mutations** — `bowire.record.start`, `bowire.mock.start`, `bowire.env.switch` return "pending confirmation" on first call; user confirms via UI affordance before commit. Maps to MCP elicitation once standardised.

### CLI — Phase 3 polish

Phase 1+2 of the `System.CommandLine` migration shipped. Deferred:

- [ ] **Tab-completion via `dotnet-suggest`** — bash / PowerShell / zsh users get free completion of every subcommand and option.
- [ ] **Per-option validators** — `--port` validated `1..65535`, `--recording` validated as `FileInfo` that exists, `--chaos` parsed ahead of dispatch.
- [ ] **Pretty-printed S.CL errors** — colorised + stderr-routed.

### HAR Import polish

`bowire import har <file.har>` CLI shipped. Remaining:

- [ ] **UI import button** — recording-manager toolbar gets "Import HAR" next to "Export HAR" / "Export JSON". File picker, client-side parse, no new server endpoint.
- [ ] **gRPC-Web detection** — classify HAR entries with `application/grpc-web` content-type + length-prefixed protobuf bodies as gRPC steps instead of REST.
- [ ] **Per-entry filter** — preview pane lists every HAR entry; checkboxes to keep only the calls that matter. Pre-uncheck obvious static-asset MIME types.
- [ ] **Merge mode** — "Append to existing recording" instead of always creating a new one.
- [ ] **Playwright integration page** — `docs/integrations/playwright.md` walking through the test → record → import → mock loop.
- [ ] **Round-trip test** — golden-file test for byte-identical HAR export of an imported HAR.

### Freeform Request Builder

Currently Bowire is discovery-first. Freeform builder flips this — the user creates a request from scratch without a discovered schema.

- [ ] **"New Request" entry point** — button in the sidebar header / command palette / landing page.
- [ ] **Protocol picker** — dropdown of supported protocols; selection drives the dynamic form fields below.
- [ ] **Dynamic input fields per protocol** — REST (verb + URL + headers + body), gRPC (URL + method + JSON body + metadata; optional `.proto` snippet for schema), GraphQL (URL + query + variables), MQTT (broker + topic + payload + QoS), SignalR (hub + method + args), WebSocket (URL + payload + subprotocol), SSE (read-only URL), MCP (URL + tool/resource/prompt + JSON).
- [ ] **No schema required** — request goes through the same `IBowireProtocol.InvokeAsync` path; plugins extended to accept ad-hoc invocations without prior discovery.
- [ ] **Save as collection item** — persist into a named collection (works with the Collections feature above).
- [ ] **History integration** — freeform requests land in the same call history as discovered-method calls.
- [ ] **Auto-discover after first call** — once a freeform request succeeds, offer to run discovery against that URL.

## Planned (no commitments yet)

- [ ] **First RC of the new versioning discipline** — features land as `1.0.x-rc.N` for a smoke round before the final `1.0.x` tag. Consumers opt in via `--prerelease`.
- [ ] **Plugin project template** — `dotnet new bowire-plugin` in the separate [Templates repo](https://github.com/Kuestenlogik/Bowire.Templates).
- [ ] **MCP SSE-transport** support — separate `/sse` event stream + message POST endpoint. Planned alongside the SSE-plugin integration via `IInlineSseSubscriber`.
- [ ] **Sidecar packaging — Docker / Compose / Kubernetes** — published `ghcr.io/kuestenlogik/bowire:latest` image already; missing: docker-compose sample in `Bowire.Samples`, Kubernetes Deployment/Pod manifest, `--url-file <path>` flag, `docs/setup/sidecar.md` walkthrough.
- [ ] **SimpleGraphQLSubscriptions sample** — hand-rolled `graphql-transport-ws` server (or HotChocolate-based) so the GraphQL plugin's subscription code path has a runnable target.
- [ ] **MCP server-side notifications via `IInlineSseSubscriber`** — close the second half of the v0.8.11 design.
- [ ] **Sidebar display: method name vs path toggle** — for REST endpoints, offer a per-sidebar toggle (sticky in localStorage) that flips the label between `GetForecast` and `GET /api/Weather/forecast/{city}`.
- [ ] **Schema watch mode** — re-discover the active server URL(s) every N seconds and show a "+ added, − removed, ~ changed" delta in the sidebar.
- [ ] **OpenAPI schema export** — inverse of the existing OpenAPI import. Generate an OpenAPI 3.1 document from the discovered REST methods so users can publish or commit a schema based on what Bowire knows.

## Recently shipped

Headlines per release; full notes at [GitHub Releases](https://github.com/Kuestenlogik/Bowire/releases).

- **v1.4.4** (2026-05-17) — Scan exit-code semantics (findings are output, not failure), Dependabot rollout across 7 sibling repos, NuGet ZIP bundle for air-gapped consumers.
- **v1.4.0–v1.4.3** — Workbench v1 (rebrand + visual-flow-editor power-ups), intercepting proxy (Stages A/B/C — capture store + SSE stream, HTTPS MITM via custom CA, workbench Proxy view), SARIF/GitHub Code Scanning compatibility chain.
- **v1.3.0** — Frame-semantics framework (Phases 1–5: annotation data model + storage + resolver, built-in detectors + frame probe, MapLibre map viewer + extension framework, split-pane layout + selection sync, `selectionMode` capability, MapLibre extracted as separate package, manual override UI, recording interpretations + replay determinism).
- **v1.2.0** — gRPC-Web transport opt-in (URL hint `grpcweb@…` + metadata header), TacticalAPI sibling-plugin preview.
- **v1.1.0** — Standalone CLI URL fix (workbench at `/`, MCP at `/mcp`), Codecov rollout across main + 5 sibling repos, MCP docs cover all four roles.
- **v1.0.x** — Custom domain (bowire.io), full release pipeline, samples page, Socket.IO namespace selection, HttpClient factory + gRPC `SocketsHttpHandler`, SignalR streaming fixes, generalised localhost-cert trust, WebSocket plugin opt-in.
- **v0.9.x** — Public go-live, Pagefind unified search across site + docs, Community page, features-page polish, navigation tidy-up, recordings format v1, mock package full plugin-isation, Surgewave tap stream rendering, UDP stream rendering, plugin enable/disable toggle, DIS stream rendering, UX iteration round 2, per-method scripts, morphdom systemic re-render, streaming UI Wireshark-style append-only list.
- **v0.8.x** — REST/OpenAPI protocol, GraphQL protocol, WebSocket protocol, MCP client + adapter, authentication helpers (v1/v2/v3), test assertions, performance graphs, request chaining + console, environments + variables, gRPC HTTP transcoding discovery, GraphQL visual selection-set picker, form-side schema validation, sidebar method search v2, streaming completion, channel lifecycle refactor, cross-cutting QoL.
- **v0.7.x** — Authentication helpers core, performance graphs, source selector, environments + variables.
- **v0.6.x** — SSE (Server-Sent Events) protocol.
- **v0.5.x** — MCP adapter (server-side bridge).
- **v0.4.x** — Plugin install system.
- **v0.3.x** — SignalR plugin, multi-protocol UI.
- **v0.2.x** — Plugin architecture.
- **v0.1.x** — Core features.

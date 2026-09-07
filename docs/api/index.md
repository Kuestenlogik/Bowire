---
summary: 'Auto-generated reference for every public type and member across the Bowire core SDK, the first-party protocol plugins, the workbench rails, and the optional infrastructure packages.'
---

# API Reference

This is the auto-generated reference for the Bowire SDK + every first-party
package that ships from this repo. It is built by DocFX from the XML
doc-comments emitted by each project's `Kuestenlogik.Bowire*.xml` file at
release time — every public type, property, method, and event lands here
verbatim.

> **Looking for prose docs?** The conceptual guides — what Bowire is, how
> the workbench is laid out, how the protocols behave, how to embed the
> SDK — live under [Features](../features/index.md), the
> [User Guide](../ui-guide/index.md), the [Protocol Guides](../protocols/index.md),
> and the [Architecture](../architecture/index.md) section. This page is
> the index for the reference surface specifically.

## How to use this reference

- **Browse by namespace.** Every namespace is listed below — pick the package
  you're working in, jump to the namespace page, drill into types.
- **Search.** The standalone HTML build wires the `_enableSearch` flag, so
  the search box across the top hits both the prose docs and this generated
  reference. The bowire.io build defers search to Pagefind across the whole
  site instead.
- **Cross-references.** Type names rendered as links resolve through DocFX's
  xref index — `<xref:Kuestenlogik.Bowire.IBowireProtocol>` in any doc page
  becomes a direct link to the interface page.

## Core

The flagship package every Bowire host references — workbench shell, plugin
registries, options, the `MapBowire()` extension, and the public extension
points sibling packages plug into.

- <xref:Kuestenlogik.Bowire> — host SDK entry point: `IBowireProtocol` contract, `BowireOptions`, `MapBowire()`, `BowireProtocolRegistry`, the plugin-loading host, the rail / module / endpoint / service contribution interfaces.
- <xref:Kuestenlogik.Bowire.Plugins> — rail / module / endpoint / service contribution registries (`BowireRailRegistry`, `BowireModuleRegistry`) and the auto-update check service.
- <xref:Kuestenlogik.Bowire.Auth> — cookie jar, mTLS handler, AWS Sig v4 signer, the auth-provider registry, the user-store SPI.
- <xref:Kuestenlogik.Bowire.Mocking> — recording / step / frame contracts shared across plugins; HAR import, schema snapshots, the mock-emitter / mock-hosting extension SPIs.
- <xref:Kuestenlogik.Bowire.Models> — service / method / message / field info DTOs the dispatcher sees (used by every protocol plugin).
- <xref:Kuestenlogik.Bowire.PluginLoading> — `AssemblyLoadContext` plumbing for `bowire plugin install` (.NET + sidecar manifests).
- <xref:Kuestenlogik.Bowire.Semantics> — semantic annotation store + the detectors that infer kinds (coordinates, timestamps, image bytes, audio bytes, …).
- <xref:Kuestenlogik.Bowire.Semantics.Extensions> — the workbench's UI-extension contract (`IBowireUiExtension`) + the embedded-asset serving helpers.
- <xref:Kuestenlogik.Bowire.Sources> — catalogue / discovery provider contract (`IBowireCatalogueProvider`) and the built-in `local` / `http` / `consul` providers.
- <xref:Kuestenlogik.Bowire.Recording> — recording-session state machine + event stream.
- <xref:Kuestenlogik.Bowire.Security> — fuzzing executor + the attack-predicate DSL the security scanner evaluates against.
- <xref:Kuestenlogik.Bowire.Projects> — where Bowire decides to put things: `BowirePaths`, the storage-scope resolver, and the `.bowire/` project manifest loader.
- <xref:Kuestenlogik.Bowire.Environments> — provisioned environments (`BowireProvisionedEnvironment`) the host seeds rather than the operator typing them.
- <xref:Kuestenlogik.Bowire.Linting> — the design-time linter behind the Lint rail: `BowireSchemaLinter`, the `.bowire/rules.json` config loader, and the `IBowireLintRule` seam.
- <xref:Kuestenlogik.Bowire.Linting.Rules> — the shipped rules (PII and sensitive response fields, missing pagination, missing versioning, string timestamps).
- <xref:Kuestenlogik.Bowire.Reporting> — the rollup shape shared by `GET /api/report/rollup`, `bowire report rollup --json` and the `bowire.report.rollup` MCP tool.
- <xref:Kuestenlogik.Bowire.Endpoints> — the rollup's HTTP surface, a thin adapter over `BowireReportReader`.
- <xref:Kuestenlogik.Bowire.Schemas> — the delta between two discovery snapshots: which services and methods were added, removed or changed.
- <xref:Kuestenlogik.Bowire.Semantics.Detectors> — the field detectors that infer semantic kinds (WGS84 coordinates, GeoJSON points, timestamps, image and audio bytes) plus the `IBowireFieldDetector` seam.
- <xref:Kuestenlogik.Bowire.Proxy> — the intercepting proxy behind `bowire proxy`: the self-signed CA that mints leaf certificates, the server, and the captured-flow store.
- <xref:Kuestenlogik.Bowire.Plugins.Sidecar> — the `sidecar.json` manifest shape and the JSON-RPC bridge that lets a non-.NET plugin implement a protocol.
- <xref:Kuestenlogik.Bowire.Net> — `BowireHttpClientFactory`, whose certificate validation consults the localhost-certificate trust list on every request.
- <xref:Kuestenlogik.Bowire.Helpers> — `SelfOriginCheck`, which tells "this serverUrl is the workbench host itself" apart from an external target.

## Workbench rails (Welle 2 layout)

After the v2.1 #325 cleanup each workbench rail ships as its own NuGet
package and registers itself via `IBowireRailContribution`. Embedded hosts
opt out by simply not referencing the package — the rail disappears from
the strip.

- <xref:Kuestenlogik.Bowire.Compose> — the Compose rail (sequenced request scenarios).
- <xref:Kuestenlogik.Bowire.Recordings> — the Recordings rail (replay-shaped capture).
- <xref:Kuestenlogik.Bowire.Flows> — the Flows rail (multi-step request chains with assertions).
- <xref:Kuestenlogik.Bowire.Benchmarking> — the Benchmarking rail (load + latency tests; renamed from `Rail.Benchmarks` in Welle 2).
- <xref:Kuestenlogik.Bowire.Interceptor> — the Proxy + Intercepted + Traffic rails consolidated into one package (Welle 2 fold-in of `Rail.Proxy` + `Rail.Intercepted` + `Rail.Traffic`).
- <xref:Kuestenlogik.Bowire.Mock> — the Mocks rail + the mock-server host (Welle 2 fold-in of `Rail.Mocks` into `Mock`).
- <xref:Kuestenlogik.Bowire.Help> — the Help rail + the `IBowireHelpProvider` contract (#324).
- <xref:Kuestenlogik.Bowire.Security.Scanner> — the Security rail + the `bowire scan` CLI: vulnerability-template + Nuclei-corpus replay, the always-on passive checks, and the **OWASP API Security Top 10 suite** (`--suite=owasp-api`, the `IOwaspApiProbe` per-entry probes, `OwaspApiCatalog`, and the `/api/security/owasp-catalog` + `/api/security/owasp-scan` workbench endpoints).
- <xref:Kuestenlogik.Bowire.Security.Templates.Nuclei> — reads the `projectdiscovery/nuclei-templates` YAML corpus and unfolds it into the scanner's template shape.
- <xref:Kuestenlogik.Bowire.Rails> — the Home, Discover and Workspaces rail descriptors (folded into Core in Welle 2 because they're always-on). The environment half of the old `Rail.Environments` lives in <xref:Kuestenlogik.Bowire.Environments>.
- <xref:Kuestenlogik.Bowire.Contracts> — the Contracts rail: a consumer → provider matrix of contract-verification results with per-interaction drill-in.
- <xref:Kuestenlogik.Bowire.Monitoring> — the Monitoring rail plus the probe scheduler, executor and signaler contracts behind `bowire monitor run`.
- <xref:Kuestenlogik.Bowire.Monitoring.Cli> — the `bowire monitor` command surface.
- <xref:Kuestenlogik.Bowire.Keyring> — the Keyring module and `IKeyringBackend`, whose shipped implementation reads the OS credential store with no NuGet dependency.
- <xref:Kuestenlogik.Bowire.Flows.Expectations> — Flow expectations: the evaluator, the per-step rollup, and the snapshot comparer.
- <xref:Kuestenlogik.Bowire.Recordings.Correlation> — the correlation analyser behind both the Timeline tab and `bowire recording correlate`.
- <xref:Kuestenlogik.Bowire.Mock.Management> — the `{basePath}/api/mocks` surface for UI-driven mock servers, plus the host manager.
- <xref:Kuestenlogik.Bowire.Mock.Matchers> — the request-matching seam (`IMockMatcher`) and what a matcher gets to reason about.
- <xref:Kuestenlogik.Bowire.Mock.Replay> — unary replay and the `{{name}}` substitution applied to a recorded body at replay time.
- <xref:Kuestenlogik.Bowire.Mock.Loading> — recording loading, format versioning, and the on-disk watcher.
- <xref:Kuestenlogik.Bowire.Mock.Chaos> — per-method fault injection (latency, faults, rule sets).
- <xref:Kuestenlogik.Bowire.Help.Provider> — the shipped `IBowireHelpProvider`, backed by the markdown embedded in the Help package.
- <xref:Kuestenlogik.Bowire.Security.Scanner.Cli> — the `bowire scan` and `bowire vulndb` command surfaces.

## First-party protocol plugins

Every protocol Bowire speaks lives in its own `Kuestenlogik.Bowire.Protocol.<Name>`
package, implementing `IBowireProtocol` plus any protocol-specific extension
points (mock hosting, schema sources, transport adapters).

- <xref:Kuestenlogik.Bowire.Protocol.Grpc> — gRPC, gRPC-Web (via `GrpcTransportMode`), descriptor-driven mock host. See the [gRPC-Web transport](../protocols/grpc.md#grpc-web-transport) section.
- <xref:Kuestenlogik.Bowire.Protocol.Rest> — REST + the OpenAPI adapter seam (`IBowireOpenApiAdapter`).
- <xref:Kuestenlogik.Bowire.Protocol.Rest.OpenApi2> — Microsoft.OpenApi 2.x adapter implementation.
- <xref:Kuestenlogik.Bowire.Protocol.Rest.OpenApi3> — Microsoft.OpenApi 3.x adapter implementation.
- <xref:Kuestenlogik.Bowire.Protocol.GraphQL> — GraphQL, including schema-driven mocks.
- <xref:Kuestenlogik.Bowire.Protocol.SignalR>
- <xref:Kuestenlogik.Bowire.Protocol.WebSocket> — includes the per-host endpoint registry + the attribute-discovered endpoint surface.
- <xref:Kuestenlogik.Bowire.Protocol.Sse> — SSE + the attribute-discovered endpoint surface.
- <xref:Kuestenlogik.Bowire.Protocol.Mqtt> — MQTT v3 / v5, including proactive emitters + reactive responders for the mock host.
- <xref:Kuestenlogik.Bowire.Protocol.Nats>
- <xref:Kuestenlogik.Bowire.Protocol.Soap>
- <xref:Kuestenlogik.Bowire.Protocol.JsonRpc>
- <xref:Kuestenlogik.Bowire.Protocol.Pulsar>
- <xref:Kuestenlogik.Bowire.Protocol.SocketIo>
- <xref:Kuestenlogik.Bowire.Protocol.OData>
- <xref:Kuestenlogik.Bowire.Protocol.Mcp> — Model Context Protocol (workbench-as-MCP-client).
- <xref:Kuestenlogik.Bowire.Protocol.Otlp> — OTLP receiver + envelope store (signals: traces, metrics, logs).
- <xref:Kuestenlogik.Bowire.Protocol.Rest.Mock> — the REST plugin's schema-only contribution to the mock server.
- <xref:Kuestenlogik.Bowire.Protocol.GraphQL.Mock> — the same for GraphQL.
- <xref:Kuestenlogik.Bowire.Protocol.Grpc.Mock> — descriptor pool, protobuf sample encoder, and the `FileDescriptorSet` → synthetic-recording builder.
- <xref:Kuestenlogik.Bowire.Protocol.Mqtt.Mock> — the embedded-broker transport host, proactive emitter, reactive responder and topic matcher.
- <xref:Kuestenlogik.Bowire.AsyncApi> — the AsyncAPI-shaped façade over the message-broker plugins.

## Optional infrastructure packages

These ship from the same repo but are reference-only — hosts pull them in
when they need the feature, otherwise the surface stays absent.

- <xref:Kuestenlogik.Bowire.Ai> — assistant runtime + provider-factory contract (`IBowireAiProviderFactory`).
- <xref:Kuestenlogik.Bowire.Ai.Anthropic> — Anthropic Claude provider.
- <xref:Kuestenlogik.Bowire.Ai.OpenAi> — OpenAI / Azure OpenAI provider.
- <xref:Kuestenlogik.Bowire.Ai.Mcp> — MCP-bridge chat client.
- <xref:Kuestenlogik.Bowire.Auth.Oidc> — OIDC auth provider implementation.
- <xref:Kuestenlogik.Bowire.Catalogue.Agent> — Bowire-agent catalogue provider (URLs from a sibling agent hub).
- <xref:Kuestenlogik.Bowire.Catalogue.Kubernetes> — k8s catalogue provider (URLs from in-cluster service discovery).
- <xref:Kuestenlogik.Bowire.Cli> — `IBowireCliCommand` contract + the assembly-scan registry; sibling packages contribute subcommands through this surface.
- `Kuestenlogik.Bowire.Map` — MapLibre-backed UI extension for `coordinate.wgs84` (and friends). Its one public type, `MapLibreExtension`, is declared in <xref:Kuestenlogik.Bowire.Semantics.Extensions>, so the reference page for it lives there.
- <xref:Kuestenlogik.Bowire.Mcp> — embed-time MCP server: turns the host's own Bowire endpoints into MCP tools.
- <xref:Kuestenlogik.Bowire.Security.Scanner> — security scanner driver + the rail contribution.
- <xref:Kuestenlogik.Bowire.Security.Templates.Nuclei> — Nuclei template → Bowire-attack translator.
- <xref:Kuestenlogik.Bowire.Telemetry> — `ActivitySource` + `Meter` plumbing for OpenTelemetry consumers of the host.
- <xref:Kuestenlogik.Bowire.Workspace.Git> — git-backed workspace store + the migration helpers.
- <xref:Kuestenlogik.Bowire.Scim> — SCIM 2.0 provisioning: the store, the filter parser, the resource shapes, and the options that gate the endpoints.
- <xref:Kuestenlogik.Bowire.Oast> — the out-of-band interaction client (`IOastClient`) the scanner's `--oast-server` talks to.
- <xref:Kuestenlogik.Bowire.Oast.Server> — the HTTP half of `bowire oast serve`: register / poll / deregister plus the callback-recording catch-all.
- <xref:Kuestenlogik.Bowire.Oast.Cli> — the `bowire oast` command surface.
- <xref:Kuestenlogik.Bowire.Monitoring.Otlp> — exports the `bowire.monitoring.*` probe metrics to an OpenTelemetry collector.
- <xref:Kuestenlogik.Bowire.Monitoring.Slack> — posts a probe transition to a Slack incoming webhook.
- <xref:Kuestenlogik.Bowire.Monitoring.PagerDuty> — triggers / resolves a PagerDuty incident on a probe transition (Events API v2).

## Public extension points

These are the seams sibling packages (and customer packages) implement to
contribute behaviour into a Bowire host. Each one is auto-discovered via
assembly scan at host startup — drop the package on the load path and the
contribution wires itself.

- <xref:Kuestenlogik.Bowire.IBowireProtocol> — wire protocol plugins. Implement to add support for a new protocol (discovery, invoke, streaming, channel open). See [Protocol Guides](../protocols/index.md) for behavioural conventions.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireRailContribution> — workbench rails. Implement to add a left-strip activity icon + its sidebar / main-pane view. See [Plugin Architecture](../architecture/plugin-architecture.md) for the rail / module split.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireModuleContribution> — cross-cutting modules (no rail icon — hooks across the shell, e.g. the AI chat pane or variable resolver).
- <xref:Kuestenlogik.Bowire.Plugins.IBowireServiceContribution> — DI service registration at `AddBowire()` time. Sibling packages register their own services through this so Core never references them at compile time.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireEndpointContribution> — ASP.NET endpoint registration at `MapBowire()` time. Sibling packages mount their admin / data endpoints under Core's auth-gated route group.
- <xref:Kuestenlogik.Bowire.Help.IBowireHelpProvider> — in-app documentation provider. Implement to back the F1 / Help drawer / `/api/help/*` endpoint stack.
- <xref:Kuestenlogik.Bowire.Sources.IBowireCatalogueProvider> — where the URL/service list comes from (local file, HTTP, Consul, k8s, agent hub).
- <xref:Kuestenlogik.Bowire.Semantics.Extensions.IBowireUiExtension> — UI extensions that mount against semantic kinds (`coordinate.wgs84`, `image.bytes`, …) as viewers or editors.
- <xref:Kuestenlogik.Bowire.Protocol.Rest.IBowireOpenApiAdapter> — version-decoupled OpenAPI parser seam so the REST plugin doesn't pin a specific `Microsoft.OpenApi` version.

> Per-extension-point how-to guides land under `docs/extending/` in a
> follow-up stream. Until they ship, the XML-doc remarks blocks on each
> interface above carry the contract (what to implement, what gets called
> when, what failure modes the host swallows).

## Polyglot sidecar plugins

Bowire also accepts non-.NET plugins via a JSON-RPC 2.0 sidecar bridge
(stdio or HTTP/SSE) — no .NET assembly, no `IBowireProtocol` implementation
needed. The wire contract, manifest schema, and packaging / install flow
(zip, `http(s)://`, `oci://`) are documented in
[Sidecar Plugins](../architecture/sidecar-plugins.md). The Python SDK lives
in its own repo:
[`Kuestenlogik/Bowire.Sdk.Python`](https://github.com/Kuestenlogik/Bowire.Sdk.Python)
(`pip install bowire-plugin`).

## Sibling protocol plugins (out-of-repo)

These ship from their own NuGet packages with independent release cadences —
see the [Protocol Guides](../protocols/index.md) for install snippets. Their
API surface is **not** part of this DocFX scope; refer to the sibling repo
for its own reference build.

- `Kuestenlogik.Bowire.Protocol.Surgewave`
- `Kuestenlogik.Bowire.Protocol.Kafka`
- `Kuestenlogik.Bowire.Protocol.Amqp`
- `Kuestenlogik.Bowire.Protocol.Dis`
- `Kuestenlogik.Bowire.Protocol.Udp`
- `Kuestenlogik.Bowire.Protocol.Akka`

> **TacticalAPI** — `Kuestenlogik.Bowire.Protocol.TacticalApi` ships from
> <https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi> on its own
> release cadence (currently v1.0.0 stable); its API surface is not part
> of this DocFX scope. See the
> [TacticalAPI protocol guide](../protocols/tacticalapi.md) for install +
> usage, and the sibling repo's README for the proto-fetch licensing
> rationale.

# Changelog

## v1.0.9-rc.2 — Socket.IO payload extraction (2026-05-04)

Found during the rc.1 smoke pass.

### Fixes
- **Socket.IO plugin: payload was `"SocketIOClient.EventContext"`**.
  The plugin called `response?.ToString()` on the SocketIOClient
  callback context, which returned the type name rather than the
  event arguments. Stream pane and unary-emit responses both lost
  the actual payload — only the event name survived. Switched to
  `IEventContext.RawText` (the JSON array `["eventName", arg1, …]`)
  with leading-element strip and single-arg unwrap so the user
  sees the same shape the underlying transport delivered.
- **Socket.IO plugin: `event` field in the form body was ignored
  for the catch-all `listen` method**. The filter only fired for
  dynamically-discovered per-event methods. Now the listen
  method also honours an `event` filter from the request body —
  empty / missing means "every event" as before.

### Tests
- Existing 712 + 7 factory tests still green; no Socket.IO tests
  added in this RC because the plugin pulls in the full
  `SocketIOClient` runtime which makes meaningful unit-tests
  fixture-heavy. Manual smoke against
  `Bowire.Samples.SocketIo/server.js` confirmed: 8 events
  captured with full `{id, shipId, dockNumber, status, at}`
  payload visible in the streaming-frame pane.

## v1.0.9-rc.1 — Shared HttpClient factory for cert-trust opt-in (2026-05-04)

First release-candidate of the cert-trust generalisation that 1.0.3
left as a follow-up. SignalR and WebSocket already honoured
`Bowire:TrustLocalhostCert` for `wss://localhost`; the
HttpClient-bearing plugins (REST, GraphQL, SSE, MCP, OData) used
the OS trust store directly and failed with a generic stream
error against the ASP.NET Core dev cert when it wasn't installed.

### Adds
- **`Kuestenlogik.Bowire.Net.BowireHttpClientFactory`**. Two
  static helpers — `Create(config, pluginId, timeout)` returns a
  ready-to-go `HttpClient`, `CreateHandler(config, pluginId)`
  returns the underlying `HttpClientHandler` for plugins that
  need to layer cookies / proxies / redirect policy on top before
  wrapping. The validation callback consults
  `LocalhostCertTrust.IsTrustedFor(...)` on every request so
  per-plugin overrides (`Bowire:rest:TrustLocalhostCert=false`
  beats `Bowire:TrustLocalhostCert=true`) keep working.
- **Defence in depth**: the relaxed validation path only fires
  when (a) the OS trust check failed AND (b) the request URL
  resolves to `localhost` / `127.0.0.1` / `::1`. A misconfigured
  host that flipped `TrustLocalhostCert=true` against an external
  hostname still validates strictly.

### Refactors (no behaviour change without the opt-in)
- **REST, GraphQL, SSE, MCP, OData** plugins switch from a
  `static readonly HttpClient s_http` to an instance `_http`
  built in `Initialize()` from the factory. HttpClient count
  per process is unchanged (one per plugin, plugin is a
  registry singleton); the only difference is the validation
  callback is now wired to config.
- **`SseSubscriber`** gains a constructor that accepts an
  externally-supplied `HttpClient`, so cross-plugin SSE
  consumers (`IInlineSseSubscriber.SubscribeAsync` from MCP /
  GraphQL graphql-sse) inherit the host's trust config.

### Tests
- 7 unit tests for `BowireHttpClientFactory`: null-config path,
  custom timeout, callback wiring, loopback-with-flag-on,
  production-URL-with-flag-on (defence-in-depth), per-plugin
  override beats global, OS-already-trusted bypass.

### Why RC, not 1.0.9 final
- Touches every HttpClient-using plugin's lifetime model. We
  want at least one round of `bowire --url https://localhost:…`
  end-to-end smoke against a non-trusted dev cert before this
  goes stable. RC drops on nuget.org with the `-rc.1` suffix
  so consumers must opt in via `--prerelease`.

### Pending
- gRPC plugin uses `SocketsHttpHandler` directly for HTTP/2 and
  has its own MTLS-handler-owner machinery, so it isn't migrated
  in this RC. Will follow once the factory grows a
  `CreateSocketsHttpHandler(...)` shape.

## v1.0.8 — Readable JSON output in stream pane (2026-05-04)

### Changes
- **`UnsafeRelaxedJsonEscaping`** for every Bowire endpoint
  response. The default `JavaScriptEncoder` escapes quotes and
  non-ASCII characters as `"` / `ü` for HTML-/script-
  injection safety, but Bowire never embeds responses inside HTML
  or `<script>` blocks — the UI fetches them as
  `application/json` only. The escapes were pure noise that made
  German / Japanese / Russian payloads, plus already-stringified
  inner JSON in event-stream frames, unnecessarily hard to read
  in the streaming-frame pane (`\\u0022` was a frequent
  side-effect of the double-serialise pattern many sample servers
  use). One-line change in `BowireEndpointHelpers.JsonOptions`.

### Notes
- The setting only changes the **wire** representation, not the
  semantic content. JSON parsers handle both forms identically;
  consumers that hand-decoded the payloads will see literal `"`
  and Unicode characters where they previously saw `\u`-escapes.
- Names of the form `*RelaxedJsonEscaping` historically alarm
  reviewers — to be explicit: this is safe in Bowire because the
  output never lands in an HTML attribute or a `<script>` body
  where browser parsers would re-interpret it.

## v1.0.7 — GraphQL/SSE streaming fixes (2026-05-04)

### Fixes
- **GraphQL plugin: subscriptions over `graphql-transport-ws`**.
  The unwrap path expected the WebSocket plugin to deliver text
  frames as escaped JSON strings, but the WebSocket plugin parses
  valid JSON into a nested `JsonElement` (so the UI shows clean
  nested objects instead of `\"`-escaped strings). The mismatch
  surfaced as `JsonElementWrongTypeException` on the very first
  `connection_ack` and bubbled up as "Stream error occurred." in
  the UI. Subscriptions now accept both shapes — the inline
  parsed object and the legacy escaped string. Bug present since
  1.0.0 (the GraphQL ws path was added before the WebSocket
  plugin's nested-JSON envelope shipped).
- **SSE plugin: `Invalid port specified` on Execute**. The frontend's
  `invokeStreaming` passes `method.name` (the human-readable label,
  e.g. `Slow keep-alive tick…`), not `method.fullName` (the
  route-bearing `SSE/events/heartbeat`). The SSE plugin's URL
  resolver expected the latter and fell through to a default that
  concatenated the prose into the URI, producing things like
  `https://localhost:5114Slow keep-alive…`. Resolver now runs the
  same discovery path as `/api/services` and matches on either
  shape — covers manual `RegisterEndpoint` *and*
  `Produces("text/event-stream")` auto-discovered routes.
- **SSE plugin: garbage `url` overrides ignored**. When an
  optional URL-override field in the form pane was left untouched,
  the form builder was echoing the schema type name `string` back
  as the value, which then concatenated to a malformed URI. The
  resolver now only honours `http(s)://…` absolutes or paths
  anchored at `/`; bare strings fall through to the discovered
  default. The form-builder echo itself is still being tracked.

### Site / docs
- **Protocol cards**: dedicated streaming screenshots replace the
  shared `streaming.png` placeholder for **GraphQL**, **SSE**, and
  **MQTT** — each card popup now shows its own protocol's frame
  pane in action (subscription emit, heartbeat tick, retained-flag
  publish).
- Capture script (`scripts/capture-builtin-screenshots.js`) gains a
  Node-side traffic generator so the GraphQL run can fire
  `updatePortCallStatus` mutations against the running sample
  without tripping the browser's same-origin policy.

## v1.0.6 — Plugin hint URL syntax + `--disable-plugin` (2026-05-04)

### Adds
- **Plugin hint URLs**: prefix any server URL with `<plugin-id>@`
  to route discovery and invoke straight to that plugin and skip
  every other plugin's probe. Example:
  `bowire --url grpc@https://api.example.com:443`. Saves the
  ~12 s gRPC HTTP/2 handshake when the URL belongs to a non-gRPC
  service. The hint is optional — `https://…` URLs without a
  hint keep the existing "probe everything" behaviour. Parser
  is careful with URI userinfo (`https://user:pwd@host`) and
  bare email-style strings (`alice@example.com`): both pass
  through untouched. Helper exposed as
  `Kuestenlogik.Bowire.BowireServerUrl.Parse`.
- **`--disable-plugin` CLI flag** (and `Bowire:DisabledPlugins`
  in appsettings.json) excludes named plugins from the assembly
  scan at startup. Use it when a plugin DLL won't load or its
  discovery probe is too slow for the current host. Repeatable
  and comma-separated forms both supported. Process-startup
  config — for per-URL plugin selection use the `hint@url`
  syntax instead.

### Notes
- The hint is opaque: validation that it names an actually-
  loaded plugin happens at the call site, not in the parser.
  An unknown hint will simply produce zero matches in the
  discovery loop and the user sees an empty service list — the
  same as a typo in `body.Protocol`.
- `BowirePluginSetting` UI toggles (per-plugin feature flags
  persisted in localStorage) are unchanged. The three layers —
  `--disable-plugin` (startup), `hint@url` (per-URL), and
  `BowirePluginSetting` (per-feature) — operate at different
  scopes and complement each other.

## v1.0.5 — SignalR no-arg streaming fix (2026-05-04)

### Fixes
- SignalR plugin's `ParseArguments` mapped a "{}" form body to a
  single positional `null` arg, which made every zero-parameter
  hub method (e.g. `SubscribeToChanges([EnumeratorCancellation]
  CancellationToken)`) fail server-side with "Failed to invoke …
  due to an error on the server" — the SignalR runtime rejected
  the wrong-argument-count call before the streamer ever ran.
  Detect the empty-body case up front and return zero args. Hub
  methods that *do* take parameters keep the existing form-unfold
  path unchanged.

## v1.0.4 — SignalR hub-URL resolution fix (2026-05-04)

### Fixes
- SignalR plugin's `ResolveHubUrl` used the service display name
  (e.g. `PortCallHub`) as the URL path even when the host had
  mapped the hub to a custom path with `app.MapHub<T>(...)`. The
  resulting GET hit the wrong path and returned 404 from the
  Negotiate endpoint, surfacing as "Stream error occurred" in
  the UI. Look up the discovered service first and use its
  Package field (the configured route) — falling back to the
  literal name only when discovery has no entry (standalone CLI
  paths). This was a regression hidden by the cert-trust fix in
  1.0.3 chasing the same symptom.

## v1.0.3 — Generalised localhost-cert trust + WebSocket plugin (2026-05-04)

### Changes
- `Bowire:SignalR:TrustLocalhostCert` is replaced by a global key:
  **`Bowire:TrustLocalhostCert`** (in `appsettings.json` or via the
  `BOWIRE__TRUSTLOCALHOSTCERT=true` env var). Every TLS-bearing
  protocol plugin reads the same flag, so a single switch covers
  the whole host. Per-plugin override `Bowire:{pluginId}:Trust
  LocalhostCert` still works as an escape hatch when one plugin
  needs different cert handling than the rest.
- WebSocket plugin gains the same opt-in: `wss://localhost` with
  the ASP.NET Core dev cert no longer fails the TLS handshake when
  the flag is on. mTLS configurations are unaffected.
- New helper `Kuestenlogik.Bowire.Auth.LocalhostCertTrust` owns the
  loopback URL check + config lookup. Plugins that need this in
  the future just call `LocalhostCertTrust.IsTrustedFor(config,
  pluginId, url)` instead of writing the same logic again.
- Defence in depth: relaxed validation only fires when the URL's
  host actually is `localhost` / `127.0.0.1` / `::1`, regardless
  of how the flag was configured.

### Pending follow-ups
- HttpClient-based plugins (REST / GraphQL / SSE / OData / MCP /
  gRPC reflection) currently rely on the OS trust store — they'll
  pick up the same opt-in once `BowireHttpClientFactory` lands as
  a shared helper. Tracked separately.

## v1.0.2 — SignalR localhost cert opt-in + mobile polish (2026-05-04)

### Fixes
- SignalR plugin: hub connections to a self-signed dev cert
  (`https://localhost`) used to fail the TLS handshake ~45 ms after
  Execute, surfacing as a generic "Stream error occurred" in the
  Bowire UI. Adds a defensive **opt-in** trust path for localhost:
  set `Bowire:SignalR:TrustLocalhostCert = true` in appsettings
  (or via the `BOWIRE__SIGNALR__TRUSTLOCALHOSTCERT` env var) and
  hub connections to `localhost` / `127.0.0.1` / `::1` skip the
  OS cert-store check. **Off by default** — production URLs always
  validate strictly, and the relaxed callback only fires when the
  URL itself is loopback. mTLS configurations are unaffected.

### Site / docs
- Mobile header: burger sits flush against the right viewport edge
  with the same optical breathing room as the logo on the left.
  Five action icons fit on a 412 px-wide phone with a tighter gap.
- Mobile burger menu: 1 px slit between the header bottom edge and
  the open submenu is gone (anchored at calc(header-height − 1px)
  so it overlaps the border).
- Mobile comparison table: only Bowire + the active competitor are
  rendered. Sticky positioning + fixed table-layout dropped, so
  the Bowire column no longer overlaps the picked competitor cell.
  Wrap forced overflow:hidden so a stray cell can't widen the body.
- Mobile section-rail (scrollspy dots + outline trigger) hidden on
  phones — the dots overlapped article text and the outline ≡
  trigger duplicated the burger menu icon.
- Pagefind search results: drop the 30 % thumbnail-column phantom
  indent (Pagefind reserves it on every result, even when no image
  is provided).
- Coming-soon protocol card: title pinned left so it matches the
  alignment of the other cards.
- Akka.NET protocol-card chip: trim "Mailbox tap (Server-streaming
  Tap/MonitorMessages)" to "Mailbox tap (Server-streaming)".

### Plugins (separate repos, no Bowire-version bump for these)
- Akka.NET 0.10.0 (already shipped in 1.0.1) — DeadLetters capture.

## v1.0.1 — Standalone first-run polish (2026-05-04)

### Fixes
- `bowire` CLI launched without `--url` and without proto uploads
  no longer blocks its sidebar on a 10 s "Loading services…"
  spinner. The gRPC plugin used to reflect against the bowire
  host's own URL (which has no `AddGrpc()`) and waited for an
  HTTP/2 handshake that resolved with `HTTP_1_1_REQUIRED` ~10 s
  later. Discovery now short-circuits to an empty list when the
  host is in `BowireMode.Standalone` with no configured sources,
  so the first-run hero (Welcome + Add server URL / Upload
  schema CTAs) renders cleanly within ~100 ms.
- Defensive whitespace-`serverUrl` skip in
  `BowireGrpcProtocol.DiscoverAsync` covers the same case if a
  future host calls a plugin with a blank URL directly.

### Site / docs
- Protocol-card popup screenshots: Akka.NET and UDP cards now
  show their own captures instead of sharing the gRPC streaming
  shot.
- Click-to-zoom lightbox for screenshots inside protocol-card
  modals.
- Capability lines in the protocol-card popup render as a row of
  rounded chips instead of a single bulleted list item.
- UI Guide layout anatomy: theme-coloured backdrop, widened
  Request Pane annotation panel, breathing room before the
  region legend.
- "Into the engine room" CTA on `/features.html` regains its
  elevated background band.
- Comparison-strip multi-protocol count bumped 10+ → 15+; Akka.NET
  sub-row added; MCP platform-ops items moved from "planned" to ✓.

### Plugins (separate repos, separate releases)
- Bowire.Protocol.Akka 0.10.0 — captures Akka.NET DeadLetters into
  the live tap stream; new `IsDeadLetter` flag on `TappedMessage`.
  Defensive try/catch in the extension constructor so a global
  `default-mailbox` swap can't NRE inside the root-guardian
  bootstrap.
- Bowire.Protocol.Kafka — new harbour-domain sample with a
  single-node KRaft Kafka compose + Confluent.Kafka producer.
- Bowire.Protocol.Udp — new harbour-domain sample with multicast
  AIS-style position pings + unicast port-call status.
- Bowire.Protocol.Dis — new harbour-domain sample replaying the
  bundled `convoy.bowire-recording.json` onto the standard DIS
  multicast group.

### Tests
- Core coverage 66.1% → 71.5% (+103 unit tests across 15 new
  files; 589 → 692). `bowire` core untouched, only test code.
- SSE test fixtures now run inside an xunit.v3 collection so the
  static endpoint registry isn't raced by parallel runners.

## v1.0.0 — Initial release (2026-05-03)

First public release of Bowire — multi-protocol API workbench for .NET.

### Highlights
- 14 packages on nuget.org under the `Kuestenlogik.Bowire.*` prefix
  (`Kuestenlogik.Bowire`, `.Tool`, `.Mock`, `.Mcp`, plus the ten
  `.Protocol.*` plug-ins).
- Embedded mode (`AddBowire()` + `MapBowire()`) and standalone CLI
  (`bowire`) ship from the same source.
- Recorder, mock server, multi-channel duplex, visual flow editor,
  per-method pre-/post-response scripts, theme-aware UI.
- Apache-2.0 licensed (`Copyright 2026 Küstenlogik`).
- Site at <https://kuestenlogik.github.io/Bowire/>.

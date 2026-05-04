# Changelog

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

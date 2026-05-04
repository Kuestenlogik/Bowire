# Changelog

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

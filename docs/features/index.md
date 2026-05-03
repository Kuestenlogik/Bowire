---
summary: 'Workflow-level reference for every feature Bowire ships.'
---

# Features

Workflow-level reference for every feature Bowire ships. Each page covers what the feature does, how to invoke it, the relevant keyboard shortcuts, and any protocol-specific caveats.

For the UI layout itself &mdash; panes, action bar, sidebar, theme &mdash; see the [User Guide](../ui-guide/index.md). For protocol-specific behaviour, see the [Protocol Guides](../protocols/index.md).

## Making requests

- [Auto-discovery](auto-discovery.md) &mdash; how services and methods are found per protocol (reflection, OpenAPI, SDL, MCP listing)
- [Form & JSON input](form-json-input.md) &mdash; switching between the schema-backed form and raw JSON editor
- [Freeform requests](freeform-requests.md) &mdash; sending ad-hoc requests without a registered schema
- [Authentication](authentication.md) &mdash; Bearer / Basic / API Key / JWT / OAuth 2.0 / AWS Sig v4 / custom token provider
- [Response chaining](response-chaining.md) &mdash; click any value in a response to paste `${response.path}` into the next request

## Streaming & channels

- [Streaming](streaming.md) &mdash; server / client / bidirectional streaming with the append-only message log
- [Duplex channels](duplex-channels.md) &mdash; long-running interactive channels (WebSocket, SignalR, MQTT, Socket.IO)
- [Multi-channel manager](multi-channel.md) &mdash; multiple concurrent streams open at once
- [Console / log view](console.md) &mdash; chronological log of every request and response

## Recording & replay

- [Recording](recording.md) &mdash; capture a sequence of calls; inspect, export, or replay them
- [Replay mock server](mock-server.md) &mdash; turn a recording into a live endpoint with `bowire mock`
- [Test assertions](test-assertions.md) &mdash; convert recording steps into CI-runnable assertions

## Automation

- [Flows](flows.md) &mdash; visual pipelines with branching, loops, assertions, and response forwarding
- [Performance graphs](performance.md) &mdash; repeat a call N times with latency percentiles (P50/P90/P99/P99.9)

## Environments & data

- [Environments & variables](environments.md) &mdash; per-environment `${var}` substitution + auth
- [Collections](collections.md) &mdash; group related requests for sharing and review
- [Favorites & history](favorites-history.md) &mdash; star methods, replay previous invocations
- [Workspace files (`.blw`)](workspace.md) &mdash; the on-disk format for collections, flows, and environments
- [Export & import](export-import.md) &mdash; HAR / cURL / Postman interchange

## UI & productivity

- [Command palette](command-palette.md) &mdash; <kbd>/</kbd>-launcher for methods, environments, protocol filters
- [Keyboard shortcuts](keyboard-shortcuts.md) &mdash; the full binding list
- [Empty-state landing](empty-state.md) &mdash; context-sensitive welcome / retry / discovery-failed screens
- [Settings dialog](settings.md) &mdash; per-plugin configuration, data paths, theme
- [Responsive & mobile](responsive-mobile.md) &mdash; how the UI adapts below 900px

## Command line & extending

- [CLI mode](cli-mode.md) &mdash; `bowire list / describe / call` for scripting and automation
- [Plugin system](plugin-system.md) &mdash; building a custom protocol plugin against `IBowireProtocol`

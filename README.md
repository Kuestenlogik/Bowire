# Bowire

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/Kuestenlogik/Bowire/branch/main/graph/badge.svg)](https://codecov.io/gh/Kuestenlogik/Bowire)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire)](https://www.nuget.org/packages/Kuestenlogik.Bowire)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire)](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

**The multi-protocol API workbench — gRPC (native + gRPC-Web + Connect), REST, GraphQL, JSON-RPC, SignalR, WebSocket, SSE, MQTT, Socket.IO, MCP, OData, AsyncAPI (discovery source), AMQP, Kafka, Akka.NET, DIS, UDP, TacticalAPI, Surgewave — runs against any service URL with zero code changes.**

[Quickstart](https://bowire.io/quickstart.html) · [Features](https://bowire.io/features.html) · [Why Bowire](https://bowire.io/why-bowire.html) · [Docs](https://bowire.io/docs/) · [Downloads](https://bowire.io/downloads.html)

[![Bowire workbench — discover, invoke, stream, record across protocols](https://raw.githubusercontent.com/Kuestenlogik/Bowire/main/site/assets/images/screenshots/ready.png)](https://bowire.io/)

## What it is

Bowire is the workbench API engineers reach for when they need to **explore, invoke, record, mock, and replay** real network traffic — locally, embedded next to an ASP.NET service, or as a sidecar in CI. No cloud account, no SaaS, no proxy: it speaks the live wire across more than ten protocols and saves what it sees in a single, replayable format.

## Install

```bash
# Cross-platform (.NET tool — recommended)
dotnet tool install -g Kuestenlogik.Bowire.Tool

# Container
docker run --rm -p 5080:5080 ghcr.io/kuestenlogik/bowire:latest
```

Windows MSIs (x64 + arm64) and self-contained portable ZIPs are
attached to every [GitHub release](https://github.com/Kuestenlogik/Bowire/releases/latest).
Winget + Homebrew submissions are in review — once they merge the
single-command installers will land here.

→ **[Read the 5-minute Quickstart](https://bowire.io/quickstart.html)** — covers the standalone CLI path *and* embedded ASP.NET integration.

## Embedded mode

Mount Bowire inside an existing ASP.NET app — discovery picks up your services from the live `IServiceProvider`:

```bash
dotnet add package Kuestenlogik.Bowire
```

```csharp
var app = WebApplication.Create(args);
app.MapBowire();   // visit /bowire
app.Run();
```

→ See [Setup → Embedded](https://bowire.io/docs/setup/embedded.html) for the per-protocol wiring.

## What's in it

- **Multi-protocol discovery** — gRPC Reflection, OpenAPI, GraphQL introspection, SignalR hub metadata, MCP `initialize`, MQTT topic scan, OData `$metadata`, AsyncAPI 2.x / 3.0 documents. One tool, one mental model.
- **All call types** — unary, server-streaming, client-streaming, duplex. Wireshark-style streaming UI with append-only frame list and per-frame detail.
- **Recordings + replay** — capture sessions, replay against any URL, export as HAR or self-contained HTML CI report.
- **Mock server** — `bowire mock --recording <file>` replays a recording as a local API endpoint. Chaos injection, schema-only mocks (OpenAPI / proto / GraphQL SDL), and capture-on-miss for spec-by-example.
- **AI bridge (MCP)** — Bowire exposes its own primitives across all three MCP halves: *tools* (`bowire.invoke`, `bowire.mock.start`, …), *resources* (`bowire://recordings`, `bowire://environments`, per-service schema dumps), and *prompts* (canned workflows like `replay-recording`, `scan-service`). Agents (Claude Desktop, Cursor, Copilot) drive Bowire directly.
- **Auth-provider extension SPI** — third extension type next to `IBowireProtocol` and `IBowireUiExtension`. `--auth-provider oidc` gates every workbench endpoint via `Kuestenlogik.Bowire.Auth.Oidc` (on Microsoft.Identity.Web), shipped as a separate NuGet so the OIDC weight only lands when you need it.
- **Plugin lifecycle in the UI + opt-in update check** — Settings → Plugins lists every installed plugin, surfaces "update available" hints, and lets you update / uninstall in place. The daily check is **opt-in** (`--update-check` or `Bowire:PluginUpdateCheck:Enabled=true`), off by default so air-gapped installs stay quiet.
- **Standalone or embedded** — `bowire` CLI tool, OCI container, or `app.MapBowire()` next to your services. Same engine.
- **Plugins** — extend with `IBowireProtocol`. Sibling-repo plugins live in their own NuGet packages so the host stays light: AMQP, Kafka, Akka.NET, DIS, UDP, TacticalAPI (Rheinmetall situational-awareness gRPC), and Surgewave tap-stream — the AMQP + TacticalAPI pair are out of RC at 1.0 as of 2026-05-26.

## Why this and not Postman / Scalar / Insomnia / Bruno / grpcurl

→ Side-by-side comparison with every major API tool — positioning,
features, license, price — lives on the site under
[Pick the right tool](https://bowire.io/#comparison).

## Documentation

- [Quickstart](https://bowire.io/quickstart.html) — zero to first call in five minutes
- [Features](https://bowire.io/features.html) — guided tour of every workbench surface
- [Setup](https://bowire.io/docs/setup/) — embedded, standalone, Docker, sidecar
- [Protocols](https://bowire.io/docs/protocols/) — per-protocol setup, conventions, gotchas
- [Architecture](https://bowire.io/docs/architecture/) — plugin model, host packages, ALC isolation
- [API reference](https://bowire.io/docs/api/) — DocFX-generated reference for every public type

## Roadmap & Community

- [ROADMAP.md](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md) — what shipped, what's planned, what's deliberately out of scope
- [Community](https://bowire.io/community.html) — Discord, Discussions, Issues, Contributions
- [CONTRIBUTING.md](https://github.com/Kuestenlogik/Bowire/blob/main/CONTRIBUTING.md) — how to write a plugin, run the smoke tests, prepare a PR

## License

[Apache 2.0](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

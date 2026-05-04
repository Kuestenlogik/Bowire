# Bowire

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire)](https://www.nuget.org/packages/Kuestenlogik.Bowire)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire)](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

**A multi-protocol API workbench for .NET — gRPC, REST, GraphQL, MQTT, SignalR, WebSocket, SSE, MCP, OData, Socket.IO, DIS, UDP — runs against any service URL with zero code changes.**

[Quickstart](https://kuestenlogik.github.io/Bowire/quickstart.html) · [Features](https://kuestenlogik.github.io/Bowire/features.html) · [Why Bowire](https://kuestenlogik.github.io/Bowire/why-bowire.html) · [Docs](https://kuestenlogik.github.io/Bowire/docs/) · [Downloads](https://kuestenlogik.github.io/Bowire/downloads.html)

[![Bowire workbench — discover, invoke, stream, record across protocols](https://raw.githubusercontent.com/Kuestenlogik/Bowire/main/site/assets/images/screenshots/ready.png)](https://kuestenlogik.github.io/Bowire/)

## What it is

Bowire is the workbench API engineers reach for when they need to **explore, invoke, record, mock, and replay** real network traffic — locally, embedded next to an ASP.NET service, or as a sidecar in CI. No cloud account, no SaaS, no proxy: it speaks the live wire across more than ten protocols and saves what it sees in a single, replayable format.

## Install

```bash
# Windows (Winget)
winget install KuestenLogik.Bowire

# macOS / Linux (Homebrew)
brew tap kuestenlogik/bowire && brew install bowire

# Cross-platform (.NET tool)
dotnet tool install -g Kuestenlogik.Bowire.Tool

# Container
docker run --rm -p 5080:5080 ghcr.io/kuestenlogik/bowire:latest
```

→ **[Read the 5-minute Quickstart](https://kuestenlogik.github.io/Bowire/quickstart.html)** — covers the standalone CLI path *and* embedded ASP.NET integration.

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

→ See [Setup → Embedded](https://kuestenlogik.github.io/Bowire/docs/setup/embedded.html) for the per-protocol wiring.

## What's in it

- **Multi-protocol discovery** — gRPC Reflection, OpenAPI, GraphQL introspection, SignalR hub metadata, MCP `initialize`, MQTT topic scan, OData `$metadata`. One tool, one mental model.
- **All call types** — unary, server-streaming, client-streaming, duplex. Wireshark-style streaming UI with append-only frame list and per-frame detail.
- **Recordings + replay** — capture sessions, replay against any URL, export as HAR or self-contained HTML CI report.
- **Mock server** — `bowire mock --recording <file>` replays a recording as a local API endpoint. Chaos injection, schema-only mocks (OpenAPI / proto / GraphQL SDL), and capture-on-miss for spec-by-example.
- **AI bridge (MCP)** — Bowire exposes its own discover / invoke / record operations as MCP tools so agents (Claude Desktop, Cursor, Copilot) can drive it directly.
- **Standalone or embedded** — `bowire` CLI tool, OCI container, or `app.MapBowire()` next to your services. Same engine.
- **Plugins** — extend with `IBowireProtocol`. First-party plugins ship as separate NuGet packages so the host stays light (DIS, UDP, Socket.IO, OData, …).

## Why this and not Postman / Scalar / Insomnia / Bruno / grpcurl

→ Side-by-side comparison with every major API tool — positioning,
features, license, price — lives on the site under
[Pick the right tool](https://kuestenlogik.github.io/Bowire/#comparison).

## Documentation

- [Quickstart](https://kuestenlogik.github.io/Bowire/quickstart.html) — zero to first call in five minutes
- [Features](https://kuestenlogik.github.io/Bowire/features.html) — guided tour of every workbench surface
- [Setup](https://kuestenlogik.github.io/Bowire/docs/setup/) — embedded, standalone, Docker, sidecar
- [Protocols](https://kuestenlogik.github.io/Bowire/docs/protocols/) — per-protocol setup, conventions, gotchas
- [Architecture](https://kuestenlogik.github.io/Bowire/docs/architecture/) — plugin model, host packages, ALC isolation
- [API reference](https://kuestenlogik.github.io/Bowire/docs/api/) — DocFX-generated reference for every public type

## Roadmap & Community

- [ROADMAP.md](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md) — what shipped, what's planned, what's deliberately out of scope
- [Community](https://kuestenlogik.github.io/Bowire/community.html) — Discord, Discussions, Issues, Contributions
- [CONTRIBUTING.md](https://github.com/Kuestenlogik/Bowire/blob/main/CONTRIBUTING.md) — how to write a plugin, run the smoke tests, prepare a PR

## License

[Apache 2.0](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

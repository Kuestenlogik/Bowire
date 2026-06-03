# Bowire

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/Kuestenlogik/Bowire/branch/main/graph/badge.svg)](https://codecov.io/gh/Kuestenlogik/Bowire)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire)](https://www.nuget.org/packages/Kuestenlogik.Bowire)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire)](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

**The multi-protocol API workbench.** gRPC, REST, GraphQL, JSON-RPC, SignalR, WebSocket, SSE, MQTT, Socket.IO, MCP, OData, AsyncAPI, AMQP, Kafka, Akka.NET, NATS, SOAP, Pulsar — run against any service URL with zero code changes.

[Quickstart](https://bowire.io/quickstart.html) · [Features](https://bowire.io/features.html) · [Why Bowire](https://bowire.io/why-bowire.html) · [Docs](https://bowire.io/docs/) · [Downloads](https://bowire.io/downloads.html) · [Bootcamp](https://bowire.io/bootcamp/)

[![Bowire workbench — discover, invoke, stream, record across protocols](https://raw.githubusercontent.com/Kuestenlogik/Bowire/main/site/assets/images/screenshots/ready.png)](https://bowire.io/)

## Install

```bash
# .NET tool (cross-platform, recommended)
dotnet tool install -g Kuestenlogik.Bowire.Tool
```

Windows MSIs, portable ZIPs, Docker images, and pending winget / Homebrew / Chocolatey distributions — all listed on the [Downloads page](https://bowire.io/downloads.html). It's the source of truth for what's available right now.

→ **[5-minute Quickstart](https://bowire.io/quickstart.html)** for the first call against your own service.

## Embedded mode

Mount the workbench inside an existing ASP.NET host:

```bash
dotnet add package Kuestenlogik.Bowire
```

```csharp
var app = WebApplication.Create(args);
app.MapBowire();   // workbench at /bowire
app.Run();
```

Discovery picks services off the host's `IServiceProvider` — REST routes, gRPC reflection, SignalR hubs, custom protocols. See [Setup → Embedded](https://bowire.io/docs/setup/embedded.html).

## Documentation

Everything user-facing lives at **[bowire.io](https://bowire.io)**. The most-asked entries:

- [Quickstart](https://bowire.io/quickstart.html) — zero to first call
- [Bootcamp](https://bowire.io/bootcamp/) — hands-on walkthrough, six units + capstone
- [Features](https://bowire.io/features.html) — workbench surfaces, recordings, mock, MCP
- [Protocols](https://bowire.io/docs/protocols/) — per-protocol setup and conventions
- [Architecture](https://bowire.io/docs/architecture/) — plugin model, host packages, ALC isolation
- [API reference](https://bowire.io/docs/api/) — DocFX-generated reference

## Roadmap & Community

- [ROADMAP.md](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md) — what shipped, what's planned, what's deliberately out of scope
- [Community](https://bowire.io/community.html) — Discord, Discussions, Issues
- [CONTRIBUTING.md](https://github.com/Kuestenlogik/Bowire/blob/main/CONTRIBUTING.md) — plugin authoring, smoke tests, PR workflow

## License

[Apache 2.0](https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE)

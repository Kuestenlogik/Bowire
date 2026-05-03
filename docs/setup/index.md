---
summary: 'Bowire runs in three deployment modes.'
---

# Deployment

Bowire runs in three deployment modes. All three support every protocol plugin &mdash; the choice is about how Bowire reaches the target API, not about what it can talk to.

| Mode | How it reaches the API | Use case |
|------|------------------------|----------|
| [Embedded](embedded.md) | Mounted at `/bowire` inside your ASP.NET app | Dev-time browser UI for your own service |
| [Standalone](standalone.md) | CLI / browser UI pointing at any remote URL | Testing third-party APIs, QA sessions, CI one-liners |
| [Docker](docker.md) | Sidecar container next to a non-.NET service | Teams running Go / Rust / Python / Node services |

## Embedded

Add Bowire directly to an ASP.NET application. The discovery pipeline reuses the host's service provider and endpoint metadata, so every protocol plugin you have installed works automatically.

```bash
dotnet add package Kuestenlogik.Bowire
```

```csharp
app.MapBowire();
```

Best when you own the service and want a zero-config UI available during development.

See [Embedded mode](embedded.md) for configuration options, custom authentication, and per-plugin settings.

## Standalone

Install Bowire as a global .NET tool. The tool ships with every protocol plugin built in and points at any URL you pass:

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire --url https://your-server
```

Best when the target service isn't yours or you don't want to modify it. Also works offline against a schema file (`.proto`, OpenAPI, GraphQL SDL) if no server is reachable.

See [Standalone tool](standalone.md) for the CLI command set and how to restrict loaded plugins.

## Docker

Ship Bowire as a sidecar container next to a non-.NET service. Planned &mdash; see the [roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md) for status.

See [Docker](docker.md) for the current integration sketch.

## Requirements

- .NET 9 or later for embedded / standalone
- Any modern browser for the UI
- For gRPC targets: the server must expose Server Reflection, **or** you drop a `.proto` file into Bowire

## Next

- [User Guide](../ui-guide/index.md) &mdash; once Bowire is running, how to drive the UI
- [Protocol Guides](../protocols/index.md) &mdash; per-protocol behaviour and setup
- [Features](../features/index.md) &mdash; workflows (recording, flows, performance, environments, &hellip;)

---
summary: 'Run Bowire as a standalone .NET global tool to browse and invoke'
---

# Standalone Tool

Run Bowire as a standalone .NET global tool to browse and invoke
**any remote API server** — no code changes required on the target
service. The tool ships with **every Bowire protocol plugin built
in** (gRPC, REST, GraphQL, SignalR, MCP, SSE, WebSocket), so a single
install gives you the full multi-protocol workbench.

![Bowire standalone tool against SimpleGrpc](../images/bowire-method-detail.png)

## Installation

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
```

The tool is published as `Kuestenlogik.Bowire.Tool` on NuGet but the executable
itself is just `bowire` — global-tool installs put it on your PATH so
you can run it from any directory.

## Browser UI mode

Launch Bowire pointed at a target server URL:

```bash
bowire --url https://my-grpc-server:443
```

Bowire starts a local HTTP server on `http://localhost:5080/bowire`
and auto-opens your default browser. The sidebar populates as
discovery completes (typically <1 second against a local server).

Multiple URLs are supported by repeating the `--url` flag — Bowire
fans out discovery in parallel and shows the merged service list
with per-URL origin tagging:

```bash
bowire --url https://api.dev.example:443 \
        --url https://api.staging.example:443
```

When discovery succeeds against some URLs but fails against others,
the empty-state landing surfaces a per-URL status table with retry
buttons for the failed ones — see the [Empty-State Landing](../features/empty-state.md)
feature page for screenshots of every state.

## Options

| Option | Description | Default |
|---|---|---|
| `--url <url>` | Server URL to discover (repeatable for multi-URL) | none |
| `--port <n>` | Bowire UI port | `5080` |
| `--title <text>` | Browser title | `Bowire` |
| `--no-browser` | Don't auto-open the browser | `false` |
| `--enable-mcp-adapter` | Expose discovered methods as MCP tools at `/bowire/mcp/sse` | `false` |

## Examples

```bash
# Custom port and title
bowire --url https://server:443 --port 8080 --title "Production API"

# Headless (e.g. inside a container) — no browser auto-open
bowire --url https://server:443 --no-browser

# Multiple URLs in one Bowire window
bowire --url https://api.dev:443 --url https://api.staging:443

# AI agent integration — exposes discovered methods as MCP tools
bowire --url https://server:443 --enable-mcp-adapter
```

## CLI mode (grpcurl-style)

The same tool also has a CLI mode for scripting and automation. No
browser is opened, no UI is started — just print to stdout:

```bash
# List all services
bowire list --url https://server:443

# Describe a specific service
bowire describe --url https://server:443 weather.WeatherService

# Invoke a method with inline JSON
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d '{"city":"Berlin"}'
```

See [CLI Mode](../features/cli-mode.md) for the full command reference.

## Plugin management

The standalone tool ships with the seven first-party protocol plugins
built in. To install **community plugins** (third-party protocols
published as `Kuestenlogik.Bowire.Protocol.*` packages on NuGet):

```bash
# Install a community plugin
bowire plugin install Kuestenlogik.Bowire.Protocol.MyProto

# Pin a specific version
bowire plugin install Kuestenlogik.Bowire.Protocol.MyProto --version 1.0.0

# List installed plugins
bowire plugin list

# Uninstall
bowire plugin uninstall Kuestenlogik.Bowire.Protocol.MyProto
```

Plugins are stored in `~/.bowire/plugins/` and loaded automatically
at the next startup.

## Run from source

If you've cloned the Bowire repository:

```bash
cd src/Kuestenlogik.Bowire.Tool
dotnet run -- --url https://my-grpc-server:443
```

The source build includes whatever protocol plugins are project-
referenced in `Kuestenlogik.Bowire.Tool.csproj` — by default that's all seven
first-party plugins.

## What discovery requires from the target

The standalone tool talks to your target server over the network and
asks it to describe itself. Each protocol has a different discovery
mechanism:

| Protocol | Discovery requirement |
|---|---|
| **gRPC** | gRPC Server Reflection enabled (`Grpc.AspNetCore.Server.Reflection`) |
| **REST** | OpenAPI / Swagger document at `/swagger/v1/swagger.json` (or another path the user uploads as a fallback) |
| **GraphQL** | `__schema` introspection enabled (Bowire queries it directly) |
| **MCP** | MCP listing endpoint reachable |
| **SignalR** | Embedded mode only — SignalR has no remote discovery protocol; for standalone, upload the hub schema as a fallback |
| **SSE** | Embedded mode only — SSE has no listing endpoint; for standalone, configure the URL manually |

If discovery fails, Bowire's landing page shows a context-sensitive
error card with the actual error message and four common-cause
troubleshoot bullets — you don't have to read logs to figure out
what's wrong. See [Empty-State Landing](../features/empty-state.md).

## See also

- [Embedded Mode](embedded.md) — drop Bowire into your own ASP.NET app
- [Docker](docker.md) — run Bowire as a container
- [CLI Mode](../features/cli-mode.md) — full CLI command reference
- [Empty-State Landing](../features/empty-state.md) — every onboarding state with screenshots

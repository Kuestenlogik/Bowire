---
summary: 'Bowire uses a plugin architecture with four extension points — protocol plugins, CLI subcommands, mock-replay emitters, and UI widgets — all discovered via assembly scanning at startup.'
---

# Plugin System

Bowire is plugin-shaped at four extension points. Each is auto-discovered via assembly scanning at startup; embedded hosts just `PackageReference` the plugin they need, standalone-CLI installs land under `~/.bowire/plugins/`. Full contract details are in [Plugin Architecture](../architecture/plugin-architecture.md):

| Extension point | What it adds | Discovered by |
|---|---|---|
| `IBowireProtocol` | A wire plugin — discover + invoke against a protocol | `BowireProtocolRegistry` |
| `IBowireCliCommand` | A `bowire <verb>` subcommand (e.g. `bowire scan`) | `BowireCliCommandRegistry` (in `Kuestenlogik.Bowire.Cli`) |
| `IBowireMockEmitter` | A replay backend for `bowire mock` recordings | The mock-server host |
| `IBowireUiExtension` | A workbench UI widget (e.g. the MapLibre map view) | `BowireExtensionRegistry` |

## Built-in Protocol Plugins

The first-party `IBowireProtocol` implementations bundled with the `bowire` tool:

| Plugin | Package | Protocol |
|--------|---------|----------|
| `BowireGrpcProtocol` | `Kuestenlogik.Bowire.Protocol.Grpc` | gRPC + gRPC-Web via Server Reflection |
| `BowireRestProtocol` | `Kuestenlogik.Bowire.Protocol.Rest` | HTTP/REST + OpenAPI / Swagger import |
| `BowireGraphQLProtocol` | `Kuestenlogik.Bowire.Protocol.GraphQL` | GraphQL queries / mutations / subscriptions |
| `BowireSignalRProtocol` | `Kuestenlogik.Bowire.Protocol.SignalR` | SignalR hub discovery |
| `BowireWebSocketProtocol` | `Kuestenlogik.Bowire.Protocol.WebSocket` | WebSocket endpoint discovery |
| `BowireSseProtocol` | `Kuestenlogik.Bowire.Protocol.Sse` | Server-Sent Events |
| `BowireMqttProtocol` | `Kuestenlogik.Bowire.Protocol.Mqtt` | MQTT 3.1.1 / 5.0 |
| `BowireSocketIoProtocol` | `Kuestenlogik.Bowire.Protocol.SocketIo` | Socket.IO namespaces + events |
| `BowireMcpProtocol` | `Kuestenlogik.Bowire.Protocol.Mcp` | Model Context Protocol for AI agents |
| `BowireODataProtocol` | `Kuestenlogik.Bowire.Protocol.OData` | OData v4 entity sets |

Sibling-repo plugins (Akka, AMQP, DIS, Kafka, Surgewave, TacticalAPI, UDP) install separately — see [Protocols overview](../protocols/index.md).

## Installing Plugins via the CLI

```bash
bowire plugin install <package-id>
bowire plugin install <package-id> --version 1.0.0
bowire plugin install <package-id> --prerelease          # 1.6.0+ — pull RC builds
bowire plugin install <package-id> --source https://nuget.internal/v3/index.json
bowire plugin list
bowire plugin list --verbose                # resolved version, sources, DLL list
bowire plugin update <package-id>           # bump one plugin to latest stable
bowire plugin update <package-id> --prerelease    # accept pre-release versions
bowire plugin update                         # bump every installed plugin
bowire plugin update <package-id> --version 2.0.0
bowire plugin inspect <package-id>          # load + print ALC + discovered IBowireProtocol types
bowire plugin uninstall <package-id>
```

The `--prerelease` flag (added in Bowire 1.6.0) opts into NuGet pre-release versions (e.g. `1.0.0-rc.2`); without it `install` / `update` resolve the latest stable. Matches `dotnet add package --prerelease` semantics.

## Sidecar (polyglot) plugins

`IBowireProtocol` is a .NET interface, but a plugin doesn't have to be a .NET assembly. Anything that speaks JSON-RPC 2.0 over **stdio** (NDJSON framing, like MCP / LSP) **or** over **HTTP + SSE** (POST request + long-lived SSE GET, like MCP's streamable-HTTP) can register as a Bowire protocol — Python, Rust, Go, Node, C++, all welcome. The host treats the sidecar like any other protocol plugin; the [Sidecar Plugins](../architecture/sidecar-plugins.md) reference has the full wire spec, manifest schema, and worked Python example.

Ship the sidecar as a `.zip` carrying a `sidecar.json` at its root. `bowire plugin install --file` accepts any of three sources for it:

```bash
bowire plugin install --file ./my-sidecar.zip                        # local path
bowire plugin install --file https://example.com/my-sidecar.zip      # http(s) URL
bowire plugin install --file oci://ghcr.io/acme/zenoh-sidecar:1.0.0  # OCI registry
```

The `oci://` form pulls straight from any OCI Distribution v2 registry (GHCR, Docker Hub, Harbor, a local `localhost:5000`, …) — anonymous pulls and the standard bearer-token dance are handled automatically. Publish a sidecar zip as a single-layer OCI artifact with [`oras`](https://oras.land):

```bash
oras push ghcr.io/acme/zenoh-sidecar:1.0.0 ./zenoh-sidecar.zip:application/zip
```

`bowire plugin list` tags sidecar entries `[sidecar: <protocol-id>]` to distinguish them from `[nuget: N files]` .NET plugins. `bowire plugin uninstall <packageId>` removes both kinds the same way.

### Writing a sidecar in Python

The official Python SDK lives at [`Kuestenlogik/Bowire.Sdk.Python`](https://github.com/Kuestenlogik/Bowire.Sdk.Python) (`pip install bowire-plugin`). Subclass `BowirePlugin`, implement `discover` / `invoke` (and optionally `invoke_stream` / `settings` / `shutdown`), then pick the transport: `run(plugin)` for stdio, `run_http(plugin, host, port)` for HTTP/SSE. Zero runtime deps, runs on Python 3.10+. Node / Go / Rust SDKs are on the roadmap.

## Plugin management via the workbench UI (1.6.0+)

The Settings → Plugins panel surfaces every installed plugin in one place. Each row shows the package id, installed version, and an "update available" hint when the configured NuGet feed has a newer one. Per-row buttons: **Update** (writes the new version into `~/.bowire/plugins/<package-id>/`) and **Uninstall** (removes the directory). A pre-release toggle at the top controls whether the latest-lookup considers RC builds.

Bundled plugins (gRPC, REST, &c — shipped inside the `bowire` tool itself) appear in the same panel with a `bundled` badge and disabled lifecycle buttons: they're updated en bloc via `dotnet tool update -g Kuestenlogik.Bowire.Tool`.

REST contract behind the panel:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/plugins` | List installed (sibling + bundled) plugins |
| `GET` | `/api/plugins/{id}/latest?prerelease=true` | Latest version on the configured feed |
| `POST` | `/api/plugins/install` | Install (body: `packageId`, optional `version`, `prerelease`) |
| `POST` | `/api/plugins/{id}/update` | Update one plugin (body: optional `version`, `prerelease`); `id="all"` updates everything |
| `DELETE` | `/api/plugins/{id}` | Uninstall |

The endpoints shell out to the in-PATH `bowire` CLI through `ProcessStartInfo.ArgumentList` so shell-metacharacters from operator input can't reach the child process. A NuGet-shape regex whitelist (`^[A-Za-z0-9][A-Za-z0-9._+-]*$`) gates `packageId` + `version` before the shell-out as defence-in-depth.

`plugin list` is a pure disk read; `plugin inspect` actually loads the plugin into a dedicated `BowirePluginLoadContext` and reflects over it — use it to confirm that a freshly-installed NuGet package exposes an `IBowireProtocol` implementation and that its private deps landed in the expected context.

`plugin update` compares the installed `resolvedVersion` (stored in `plugin.json`) against what the configured sources advertise and skips cleanly when they match. When moving between versions, the current install is replaced in-place — same directory, fresh DLL set.

Plugins are stored in `~/.bowire/plugins/` with per-plugin subdirectories. Each plugin includes a `plugin.json` manifest tracking the package ID, version, install date, and included files.

Install uses the `NuGet.Protocol` client directly — no `dotnet restore/build` detour — so the host only needs the .NET **runtime** (not the SDK) and downloads skip the temp-csproj song-and-dance. Transitive runtime dependencies follow automatically; host-provided assemblies (`Kuestenlogik.Bowire*`, `System.*`, `Microsoft.*`, `NETStandard.*`) are filtered out at copy time so they don't shadow the loaded host versions.

### Private feeds and multiple sources

Both `--source` on the CLI (repeatable) and `Bowire:Plugin:Sources` in `appsettings.json` feed into the same source list. When unset, nuget.org is the default. The first feed that has the package wins — put private feeds first if you want to shadow public versions.

```bash
# Single alternative feed
bowire plugin install MyCompany.Internal.Plugin --source https://nuget.corp.local/v3/index.json

# Multiple feeds (private first, public fallback)
bowire plugin install MyCompany.Plugin \
  --source https://nuget.corp.local/v3/index.json \
  --source https://api.nuget.org/v3/index.json
```

```json
// appsettings.json
{
  "Bowire": {
    "Plugin": {
      "Sources": [
        "https://nuget.corp.local/v3/index.json",
        "https://api.nuget.org/v3/index.json"
      ]
    }
  }
}
```

Retyping `--source` on the CLI replaces the appsettings list entirely — same semantics as `--url` in the browser UI.

## Configuring the Plugin Path

The plugin directory is resolved from a standard .NET configuration stack (highest priority wins):

1. `--plugin-dir <path>` CLI flag (applies to every subcommand, top-level)
2. `BOWIRE_PLUGIN_DIR` environment variable
3. `appsettings.json` key `Bowire:PluginDir`
4. Default `~/.bowire/plugins/`

```bash
# All three pick the same directory — use whichever fits your workflow.
bowire plugin list --plugin-dir ./my-plugins
BOWIRE_PLUGIN_DIR=./my-plugins bowire plugin list
echo '{ "Bowire": { "PluginDir": "./my-plugins" } }' > appsettings.json
```

Install, list, uninstall, and the runtime plugin-load at startup all agree on the same resolved path.

### Embedded Hosts

Applications that embed Bowire via `AddBowire()` can wire the same plugin directory through `IConfiguration`:

```csharp
builder.Services
       .AddBowirePlugins(builder.Configuration)   // reads Bowire:PluginDir
       .AddBowire();
```

Non-existent paths are a no-op so the call is safe to make unconditionally.

## Per-Plugin Configuration

Plugins bind their own configuration section under `Bowire:Plugins:<PluginName>` via the standard .NET options pattern. Inside the plugin's `IBowireProtocolServices.ConfigureServices`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddOptions<MqttPluginOptions>()
            .BindConfiguration("Bowire:Plugins:Mqtt");
}
```

The options class is then injectable as `IOptions<MqttPluginOptions>` anywhere in the plugin. Users configure it per the standard .NET precedence — CLI args → env vars → appsettings.json:

```json
{
  "Bowire": {
    "PluginDir": "./my-plugins",
    "Plugins": {
      "Mqtt": {
        "BrokerPort": 1883,
        "DefaultTopic": "sensors/#"
      }
    }
  }
}
```

`BindConfiguration` resolves `IConfiguration` lazily at options-resolution time — no changes to the `IBowireProtocolServices` interface or to `AddBowire` required.

## Browser-UI Options from Config

The browser-UI mode (plain `bowire` without a subcommand) also reads its own options from the shared config stack. Everything that used to need a dedicated CLI flag now has a matching `Bowire:*` key:

| CLI flag | Config key | Default |
|---|---|---|
| `--port`, `-p` | `Bowire:Port` | `5080` |
| `--title` | `Bowire:Title` | `"Bowire"` |
| `--url`, `-u` (repeatable) | `Bowire:ServerUrl`, `Bowire:ServerUrls` (array) | empty |
| `--no-browser` | `Bowire:NoBrowser` | `false` |
| `--enable-mcp-adapter` | `Bowire:EnableMcpAdapter` | `false` |

```json
{
  "Bowire": {
    "Port": 7070,
    "Title": "Staging API Browser",
    "ServerUrls": [
      "https://api-staging.example.com",
      "https://api-canary.example.com"
    ],
    "NoBrowser": true
  }
}
```

CLI flags still override appsettings (standard .NET precedence), so the same config file can hold defaults while one-off invocations retype `--port 8080` to override without editing the file. Repeated `--url` flags replace the appsettings list entirely — retyping `--url` is a full override, not an append.

## How Discovery Works

1. `MapBowire()` triggers `BowireProtocolRegistry.Discover()`
2. The registry scans all loaded assemblies for types implementing `IBowireProtocol`
3. Each plugin is instantiated and its `Initialize(IServiceProvider?)` method is called
4. The registry exposes all plugins to the Bowire API endpoints

Installed CLI plugins are loaded into a dedicated `AssemblyLoadContext` per plugin directory (see below).

## Isolation

Each installed plugin lives in its own `BowirePluginLoadContext` rather than sharing the default ALC. That gives two guarantees:

- **Plugin-private dependencies coexist.** Plugin A can ship MQTTnet 5.1 while Plugin B ships 5.2 — each context loads its own copy from its own folder without collision.
- **Contract types stay identical.** Assembly names starting with `Kuestenlogik.Bowire*`, `System.*`, `Microsoft.*`, or `NETStandard.*` delegate to the default ALC, so the `IBowireProtocol` interface in Plugin A's assembly is the *same* type as the one in the host — `typeof(IBowireProtocol).IsAssignableFrom(pluginType)` just works, reflection discovery finds plugin types, DI flows normally.

`BowirePluginLoadContext` is public, so embedded hosts can reuse it and extend the shared-prefix list with their own SDK namespace:

```csharp
var ctx = new BowirePluginLoadContext(pluginDir, additionalSharedPrefixes: new[] { "Acme.Corp." });
```

Hot unload / replace isn't supported yet (`IsCollectible = false`). The `bowire plugin update` path will add that once the update subcommand lands.

## Writing a Custom Plugin

Implement `IBowireProtocol` and package it as a NuGet package. See [Building Custom Protocols](../protocols/custom.md) for a complete guide.

The fastest way to get a working skeleton is the `dotnet new` template maintained in the [**Bowire.Templates**](https://github.com/Kuestenlogik/Bowire.Templates) sister repo:

```bash
dotnet new install Kuestenlogik.Bowire.Templates
dotnet new bowire-plugin --name <Org>.Bowire.Protocol.<YourProtocol>
```

Both `<Org>` and `<YourProtocol>` are placeholders — replace `<Org>` with your organisation prefix (e.g. `Acme`, `MyCompany`, `com.yourname`) and `<YourProtocol>` with the protocol you're adding (`Amqp`, `Nats`, `Stomp`, …). The convention `<Org>.Bowire.Protocol.<Name>` keeps the package discoverable alongside the bundled `Kuestenlogik.Bowire.Protocol.*` plugins but isn't enforced — any package id works.

Concrete example:

```bash
dotnet new bowire-plugin --name Acme.Bowire.Protocol.Amqp
dotnet pack ./Acme.Bowire.Protocol.Amqp
bowire plugin install Acme.Bowire.Protocol.Amqp --source ./nupkgs
```

It scaffolds the csproj with the right `Kuestenlogik.Bowire` reference, a sample `IBowireProtocol` implementation, and a test project.

See also: [Plugin Architecture](../architecture/plugin-architecture.md), [Custom Protocols](../protocols/custom.md)

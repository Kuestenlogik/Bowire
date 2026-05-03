---
summary: 'Bowire uses a plugin architecture based on the IBowireProtocol interface.'
---

# Plugin System

Bowire uses a plugin architecture based on the `IBowireProtocol` interface. Protocol plugins are auto-discovered via assembly scanning at startup -- no manual registration is needed.

## Built-in Plugins

| Plugin | Package | Protocol |
|--------|---------|----------|
| `BowireGrpcProtocol` | `Kuestenlogik.Bowire.Protocol.Grpc` | gRPC via Server Reflection |
| `BowireSignalRProtocol` | `Kuestenlogik.Bowire.Protocol.SignalR` | SignalR hub discovery |
| `BowireSseProtocol` | `Kuestenlogik.Bowire.Protocol.Sse` | Server-Sent Events |
| `BowireMcpProtocol` | `Kuestenlogik.Bowire.Protocol.Mcp` | Model Context Protocol for AI agents |

## Installing Plugins

Third-party protocol plugins can be installed via the CLI:

```bash
bowire plugin install <package-id>
bowire plugin install <package-id> --version 1.0.0
bowire plugin install <package-id> --source https://nuget.internal/v3/index.json
bowire plugin list
bowire plugin list --verbose                # resolved version, sources, DLL list
bowire plugin update <package-id>           # bump one plugin to latest
bowire plugin update                         # bump every installed plugin
bowire plugin update <package-id> --version 2.0.0
bowire plugin inspect <package-id>          # load + print ALC + discovered IBowireProtocol types
bowire plugin uninstall <package-id>
```

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

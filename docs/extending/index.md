---
title: Extend Bowire
summary: 'Every seam Bowire exposes to sibling packages — rails, modules, protocol plugins, UI extensions, help providers — with a how-to guide per extension point and a verified link back to the interface in the API reference.'
---

# Extend Bowire

Bowire's workbench is assembled from contributions: every rail in the strip, every protocol Bowire speaks, every viewer / editor that mounts on a payload, and the in-app docs surface are all loaded through public extension points. Sibling packages declare a contribution, Core's `AddBowire()` / `MapBowire()` pair auto-discovers it, and the workbench wires it into the shell.

This section has one how-to per extension point. Each guide quotes the actual interface from `src/`, walks through a working in-repo example, and shows the registration path Core uses to pick the contribution up.

## Pick your seam

| If you want to … | Implement | Guide |
|---|---|---|
| Add a new top-level activity (icon in the rail strip + sidebar + main pane) | `IBowireRailContribution` | [Build a rail](rail.md) |
| Add support for a new wire protocol (discovery, invoke, streaming, channel) | `IBowireProtocol` | [Build a protocol plugin](protocol.md) |
| Mount a viewer or editor against a semantic kind (`coordinate.wgs84`, …) | `IBowireUiExtension` | [Build a UI extension](ui-extension.md) |
| Add a cross-cutting module (no rail icon, hooks the shell — AI, var resolver, …) | `IBowireModuleContribution` | [Build a Settings module](module.md) |
| Ship in-app documentation (F1 / Help rail / `/api/help/*`) | `IBowireHelpProvider` | [Ship a help provider](help-provider.md) |

## How discovery works

Every extension point above is auto-discovered by Core. The mechanism is the same across all of them:

1. **Assembly scan.** `BowireServiceCollectionExtensions.AddBowire` force-loads every `Kuestenlogik.Bowire*.dll` it finds next to the entry assembly, then walks `AppDomain.CurrentDomain.GetAssemblies()` filtered to assemblies whose name contains `Bowire`.
2. **Type filter.** For each assembly, the relevant registry (`BowireProtocolRegistry`, `BowireRailRegistry`, `BowireModuleRegistry`, the extension scan over `[BowireExtension]`) walks the public types and picks the ones that implement / are tagged with the contract.
3. **Instantiate.** Every match is constructed via `Activator.CreateInstance` — implementations **must** expose a parameterless constructor. Exceptions thrown from the constructor are caught and logged, never propagated, so one bad sibling can't take down host startup.
4. **Register.** The contribution lands in its registry; the workbench picks it up the next time it renders (rails seed `__BOWIRE_CONFIG__.rails`, modules seed `__BOWIRE_CONFIG__.modules`, protocols feed `BowireProtocolRegistry`, UI extensions surface through `/api/ui/extensions`).

The single hard requirement is that **your sibling assembly must be on disk next to the host's binary** when `AddBowire()` runs. The standalone Tool, the Docker image, and the NuGet-referenced embedded host all satisfy that — `dotnet add package` puts the dll next to the host, `bowire plugin install` puts it under `~/.bowire/plugins`, and `AddBowirePlugins(pluginDir)` loads it before `AddBowire`.

## Explicit registration (override / out-of-tree)

For the descriptor-shaped contributions (rails, modules) Core also exposes a DI helper so a host can register a contribution explicitly — useful when overriding a built-in's metadata or when the descriptor lives in an assembly the auto-scan wouldn't otherwise reach:

```csharp
builder.Services.AddBowireRail<MyCustomRail>();
builder.Services.AddBowireModule<MyCustomModule>();
```

Both helpers live on `BowireServiceCollectionExtensions`. The registry de-duplicates by `Id` (last write wins) so a host can override a built-in rail's label / icon / sort by registering a replacement descriptor with the same id.

## Cross-references

Every interface quoted in the guides below has a stable namespace and lives in `Kuestenlogik.Bowire.dll`. The auto-generated reference index pins them under their namespaces:

- <xref:Kuestenlogik.Bowire.IBowireProtocol>
- <xref:Kuestenlogik.Bowire.Plugins.IBowireRailContribution>
- <xref:Kuestenlogik.Bowire.Plugins.IBowireModuleContribution>
- <xref:Kuestenlogik.Bowire.Semantics.Extensions.IBowireUiExtension>
- <xref:Kuestenlogik.Bowire.Help.IBowireHelpProvider>
- <xref:Kuestenlogik.Bowire.Plugins.IBowireServiceContribution> — DI registration at `AddBowire()` time, used by Welle 2 sibling packages (e.g. Interceptor) instead of taking a compile-time Core reference.
- <xref:Kuestenlogik.Bowire.Plugins.IBowireEndpointContribution> — ASP.NET endpoint mounting at `MapBowire()` time.

See the [API reference index](../api/index.md) for the complete extension-point catalogue.

## What this section does not cover

- **MCP tools.** Bowire's MCP surface (`BowireMcpTools` in `Kuestenlogik.Bowire.Mcp`) is built on the official `ModelContextProtocol` SDK; tools are class methods tagged with `[McpServerToolType]` + `[McpServerTool]` and registered via the SDK's `WithTools<T>()` builder, not a Bowire-owned extension point. Adding a new tool surface means writing a class with the SDK attributes and calling `WithTools<YourClass>()` next to the existing `AddBowireMcp()` chain — see the docstring on `BowireMcpServiceCollectionExtensions.AddBowireMcp` for the wiring shape.
- **Polyglot sidecar plugins.** Non-.NET protocol plugins use a JSON-RPC bridge instead of `IBowireProtocol`; see [Sidecar Plugins](../architecture/sidecar-plugins.md).

---
title: Customizing the embedded workbench
summary: "Pick exactly the rails, modules, and protocols your embedded host ships — from the everything bundle down to a hand-picked package set."
---

# Customizing the embedded workbench

The workbench is assembled from **packages**, not baked into one binary. Core (`Kuestenlogik.Bowire`) provides the shell — the rail strip, the sidebar/main dispatch, discovery, the Home rail — and every other surface (Compose, Recordings, Flows, Benchmarks, Mocks/Intercept, Workspaces, Security, Help, the AI assistant, each protocol) ships as its own package that **contributes** itself when referenced. Reference a package and its rail appears; drop it and the rail — and its JS, endpoints, and dependencies — is simply absent.

This is the same [plugin architecture](../architecture/plugin-architecture.html) the CLI uses; embedding just chooses the reference set at build time.

## The three ways to pick packages

### 1. The everything bundle

```xml
<PackageReference Include="Kuestenlogik.Bowire.Bundle.Workbench" />
```

`Bundle.Workbench` is a meta-package (no code — only references) that pulls in Core + every Rail + every Module + the AI stack + the full protocol set. It's what the standalone `bowire` tool ships. Use it when you want the complete workbench in your host and don't care about trimming.

### 2. The minimal bundle

```xml
<PackageReference Include="Kuestenlogik.Bowire.Bundle.Minimal" />
```

`Bundle.Minimal` is Core + the common protocols (REST, gRPC, GraphQL, WebSocket, SSE) and nothing else — no Benchmarks, no Mocks, no AI. A lean starting point for a host that only needs "discover + call".

### 3. Hand-picked packages

Reference Core plus exactly the rails and protocols you want:

```xml
<PackageReference Include="Kuestenlogik.Bowire" />
<PackageReference Include="Kuestenlogik.Bowire.Protocol.Rest" />
<PackageReference Include="Kuestenlogik.Bowire.Recordings" />
<PackageReference Include="Kuestenlogik.Bowire.Flows" />
<!-- no Benchmarks, no Mocks, no AI, no Security rail -->
```

`app.MapBowire()` discovers whatever's referenced. The rail strip renders only the rails whose packages are present; the sidebar/main dispatch resolves each rail's renderer at runtime, so an absent rail leaves no dangling arm.

## Rails that stay in Core

A few rails are **core-resident by design** — their JS ships inside Core, not a droppable package, because they're the workbench's navigation spine rather than optional feature surfaces:

- **Home** — the cross-workflow launchpad; the default landing.
- **Discover** — the service/method tree; the reason the workbench exists.
- **Workspaces** — the workspace-navigation hub. Its main pane isn't a self-contained rail: it dispatches into the *workspace-scoped* Collections, Environments, Recordings, Sources, and Settings sub-views (it even reuses the Sources detail renderer). Pulling it into a package would drag those cross-rail surfaces along, so it belongs with Home/Discover.

These are always present, so there's no pluggability to gain by extracting them — and their dispatch naming stays honest (Core always ships them). Every *optional* feature rail below is a droppable package.

## What each package contributes

| Package | Rail / surface |
|---------|----------------|
| `Kuestenlogik.Bowire` | Core shell + Home + Discover + Workspaces rails |
| `…Compose` | Compose (request builder, collections, presets) |
| `…Recordings` | Recordings |
| `…Flows` | Flows (+ the `bowire test` flow runner) |
| `…Benchmarking` | Benchmarks |
| `…Mock` | Mock servers (surfaced under the Intercept rail) |
| `…Interceptor` | Intercept (captured traffic, live overrides) |
| `…Security.Scanner` | Security rail + `bowire scan` |
| `…Help` | Help rail |
| `…Ai` (+ `.OpenAi` / `.Anthropic` / `.Mcp`) | AI assistant module |
| `…Protocol.<name>` | One discovery/invoke protocol each |

## How a rail plugs in (for package authors)

A rail is a class implementing `IBowireRailContribution`, auto-discovered from the loaded assemblies. The descriptor declares the rail's id, icon, sort order, sidebar kind — and, since the pluggable-workbench cut-over, the **renderer keys** that wire its JS to core without core naming the rail:

```csharp
public sealed class MyRailContribution : IBowireRailContribution
{
    public string Id => "myrail";
    public string DisplayName => "My rail";
    public string IconKey => "activity";
    public int SortIndex => 500;

    // #314 renderer-key seam — core resolves these at render time from
    // window.__bowireRailRenderers instead of a hardcoded dispatch arm.
    public string? SidebarRendererKey => "myRailSidebar";
    public string? MainPaneRendererKey => "myRailMain";
}
```

The package's embedded JS fragment registers the matching renderers:

```js
window.__bowireRailRenderers = window.__bowireRailRenderers || {};
window.__bowireRailRenderers.myRailSidebar = renderMyRailSidebar;
window.__bowireRailRenderers.myRailMain = renderMyRailMain;
```

Core's `renderSidebar()` / `renderMain()` give a rail's registered renderer first crack (resolved by the descriptor key); when the package isn't loaded, there's no rail and no renderer — nothing to fall through to. The JS fragment is spliced into the workbench bundle at build time (see the `_rail-fragments-marker.js` stitching contract), so no separate script tag or asset wiring is needed.

## See also

- [Plugin architecture](../architecture/plugin-architecture.html) — the discovery + ALC-isolation model rails and protocols share.
- [Packages](../architecture/packages.html) — the full package map.
- [Embedded setup](../setup/embedded.html) — mounting `MapBowire()` in your host.

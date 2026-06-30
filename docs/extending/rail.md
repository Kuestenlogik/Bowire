---
title: Build a rail
summary: 'Implement IBowireRailContribution to add a top-level activity to the workbench shell — a rail-strip icon, its sidebar template, and an optional main-pane renderer.'
---

# Build a rail

A **rail** is one of the top-level activities you see in the left strip of the workbench: Home, Discover, Compose, Workspaces, Recordings, Mocks, Flows, Proxy, Benchmarks, Security, Help. Each one owns an icon in the strip, a sidebar (left of the splitter), and a main pane (right of the splitter). Reach for this seam when your package brings a new top-level surface; reach for [a module](module.md) instead when you only need to hook the existing shell.

## The interface

`IBowireRailContribution` lives in `src/Kuestenlogik.Bowire/Plugins/IBowireRailContribution.cs`. Every member below comes straight from that file:

```csharp
public interface IBowireRailContribution
{
    string Id { get; }
    string DisplayName { get; }
    string IconKey { get; }
    int SortIndex { get; }
    string Group { get; }
    string SidebarKind { get; }

    bool DefaultEnabled => true;
    bool AlwaysOn => false;
    bool HideFromRail => false;
    bool RequiresWorkspace => false;

    string? SidebarRendererKey => null;
    string? MainPaneRendererKey => null;
}
```

What each member is for:

- **`Id`** — stable case-sensitive snake-lower identifier (e.g. `"recordings"`, `"security"`). Matches the rail-mode id the JS bundle keys routing off; operators' `localStorage.bowire_rail_mode` value and any deep links (`?rail=help&topic=…`) reference this string.
- **`DisplayName`** — human-readable label for the rail tooltip and the Settings → Rail modes list.
- **`IconKey`** — name from the workbench's SVG-icon catalogue (e.g. `"house"`, `"discover"`, `"shield"`, `"info"`). Resolved JS-side via `svgIcon(key)`; an unknown key falls back to a generic square placeholder.
- **`SortIndex`** — lower-first. The built-in catalogue uses 100-step intervals so third-party rails can wedge between two built-ins without a renumber.
- **`Group`** — visual group for the rail-strip divider logic. Adjacent rails with different groups draw a separator. Built-in groups: `"work"`, `"scenarios"`, `"quality"`, `"hardening"`.
- **`SidebarKind`** — sidebar template the rail renders. Recognised values are listed in the XML doc-comment on the interface (`"none"`, `"services"`, `"collections"`, `"environments"`, `"recordings"`, `"mocks"`, `"workspaces"`, `"sources"`, `"benchmarks"`, `"flows"`, `"proxy"`, `"security"`, `"library"`). Adding a new value requires a matching arm in `render-sidebar.js`.
- **`DefaultEnabled`** — defaults to `true`; set `false` for a rail an operator might reasonably switch off. `AlwaysOn` (defaults `false`) makes the toggle non-disablable in Settings; only Home, Discover, Compose, Workspaces ship as always-on.
- **`HideFromRail`** — keep the rail in the catalogue (so other rails can dispatch into it) without rendering a strip icon.
- **`RequiresWorkspace`** — when `true`, clicking the rail without an active workspace redirects to Home and fires an explanatory toast. Use for rails that persist artefacts to a workspace folder.
- **`SidebarRendererKey` / `MainPaneRendererKey`** — optional names of JS functions your rail's bundle registered on `window.__bowireRailRenderers`. Lets your package own its sidebar / main-pane render instead of falling back to Core's dispatcher arm. Convention: `railId + 'Sidebar'` / `railId + 'Main'`.

## Minimal working example

The Recordings rail (`src/Kuestenlogik.Bowire.Recordings/BowireRecordingsRailContribution.cs`) is the cleanest in-repo template:

```csharp
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Recordings;

public sealed class BowireRecordingsRailContribution : IBowireRailContribution
{
    public string Id => "recordings";
    public string DisplayName => "Recordings";
    public string IconKey => "recording";
    public int SortIndex => 600;
    public string Group => "scenarios";
    public string SidebarKind => "recordings";
    public bool RequiresWorkspace => true;
}
```

That's the whole descriptor. The actual sidebar + main-pane DOM lives in Core's `render-sidebar.js` / `render-main.js` switch arms under the `"recordings"` kind — the descriptor only opts the rail into the strip and Settings catalogue.

For a rail whose render lives in its own package's JS bundle, look at how `BowireHelpRailContribution` (`src/Kuestenlogik.Bowire.Help/BowireHelpRailContribution.cs`) declares its strip slot at the bottom of the rail (`SortIndex => 9500`, own `Group => "help"`) while delegating the actual rendering to Core's `help.js` via `SidebarKind => "help"`.

## Registration

There are two paths, and a sibling package usually picks the first:

**1. Auto-discovery (the common case).** Drop the contribution type into a `Kuestenlogik.Bowire.*` assembly with a parameterless constructor. `BowireRailRegistry.Discover` (called from `AddBowire()` via the rail seed) walks every loaded `Kuestenlogik.Bowire*` assembly, picks every concrete type that implements `IBowireRailContribution`, instantiates it via `Activator.CreateInstance`, and registers it. The Recordings example above doesn't need any wiring beyond the file existing in the `Kuestenlogik.Bowire.Recordings` assembly.

**2. Explicit registration.** When you want to override a built-in's metadata (the registry de-duplicates by `Id`, last write wins) or your descriptor lives in an assembly the auto-scan won't reach:

```csharp
builder.Services.AddBowireRail<MyCustomRail>();
```

`AddBowireRail<TRail>()` is defined on `BowireServiceCollectionExtensions` and requires `TRail : class, IBowireRailContribution, new()`.

Either way the JS bundle sees the new rail through `__BOWIRE_CONFIG__.rails` (built by `BowireRailRegistry.ToJson()` at request time): `{ id, label, icon, group, sort, sidebar: { kind }, hideFromRail, alwaysOn, defaultEnabled, requiresWorkspace }` plus the renderer-key fields when set.

## See also

- <xref:Kuestenlogik.Bowire.Plugins.IBowireRailContribution> — the auto-generated interface reference.
- [Plugin architecture](../architecture/plugin-architecture.md) — the rail / module / endpoint / service contribution split.
- [Rail strip](../features/rail-strip.md) — what the workbench draws from the seeded rail catalogue.
- [Build a Settings module](module.md) — when you want to hook the shell without owning a rail icon.

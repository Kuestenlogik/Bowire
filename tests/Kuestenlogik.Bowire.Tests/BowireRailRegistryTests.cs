// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Compose;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Rails;
using Kuestenlogik.Bowire.Workspaces;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for #294 — <see cref="BowireRailRegistry"/> +
/// <see cref="BowireModuleRegistry"/>. Covers the register / lookup
/// surface + the JSON-emit shape that the HTML generator embeds
/// into <c>__BOWIRE_CONFIG__.rails</c> / <c>.modules</c>. Discovery
/// via assembly scan is exercised indirectly by the integration tests.
/// </summary>
public class BowireRailRegistryTests
{
    private sealed class StubRail(string id, int sort = 100, bool alwaysOn = false, bool hideFromRail = false, bool requiresWorkspace = false) : IBowireRailContribution
    {
        public string Id { get; } = id;
        public string DisplayName => $"{Id[..1].ToUpperInvariant()}{Id[1..]}";
        public string IconKey => "square";
        public int SortIndex { get; } = sort;
        public string Group => "work";
        public string SidebarKind => "none";
        public bool AlwaysOn { get; } = alwaysOn;
        public bool HideFromRail { get; } = hideFromRail;
        public bool RequiresWorkspace { get; } = requiresWorkspace;
    }

    private sealed class StubModule(string id) : IBowireModuleContribution
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
    }

    [Fact]
    public void Register_Adds_To_Rails_List()
    {
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("alpha"));

        Assert.Single(registry.Rails);
        Assert.Equal("alpha", registry.Rails[0].Id);
    }

    [Fact]
    public void Register_Sorts_By_SortIndex_Then_Id()
    {
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("gamma", sort: 100));
        registry.Register(new StubRail("alpha", sort: 200));
        registry.Register(new StubRail("beta", sort: 100));

        Assert.Collection(registry.Rails,
            r => Assert.Equal("beta", r.Id),
            r => Assert.Equal("gamma", r.Id),
            r => Assert.Equal("alpha", r.Id));
    }

    [Fact]
    public void Register_Same_Id_Replaces_Existing_Descriptor()
    {
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("home", sort: 100));
        registry.Register(new StubRail("home", sort: 500));

        Assert.Single(registry.Rails);
        Assert.Equal(500, registry.Rails[0].SortIndex);
    }

    [Fact]
    public void GetById_Returns_Null_When_Missing()
    {
        var registry = new BowireRailRegistry();
        Assert.Null(registry.GetById("missing"));
    }

    [Fact]
    public void GetById_Case_Insensitive_Lookup()
    {
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("home"));

        Assert.NotNull(registry.GetById("HOME"));
    }

    [Fact]
    public void ToJson_Emits_Empty_Array_For_Empty_Registry()
    {
        var registry = new BowireRailRegistry();
        Assert.Equal("[]", registry.ToJson());
    }

    [Fact]
    public void ToJson_Carries_AlwaysOn_HideFromRail_Flags()
    {
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("home", alwaysOn: true));
        registry.Register(new StubRail("collections", hideFromRail: true));

        var json = registry.ToJson();
        Assert.Contains("\"id\":\"home\"", json, StringComparison.Ordinal);
        Assert.Contains("\"alwaysOn\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"hideFromRail\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_Carries_RequiresWorkspace_Flag()
    {
        // render-sidebar.js reads __BOWIRE_CONFIG__.rails[i].requiresWorkspace
        // on every rail click to decide whether to redirect to Home + fire
        // a 'create a workspace first' toast. If this key disappears from
        // the JSON, the guard silently degrades and the no-workspace UX
        // regression returns — operators get dropped into an empty rail
        // surface with no hint about why.
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("recordings", requiresWorkspace: true));
        registry.Register(new StubRail("home"));

        var json = registry.ToJson();
        Assert.Contains("\"id\":\"recordings\"", json, StringComparison.Ordinal);
        Assert.Contains("\"requiresWorkspace\":true", json, StringComparison.Ordinal);
        // Default for non-workspace-bound rails is false so the JS guard
        // sees an explicit "no, this works standalone" signal rather than
        // a missing key (which would also fail !!r.requiresWorkspace, but
        // being explicit makes the contract debuggable from devtools).
        Assert.Contains("\"requiresWorkspace\":false", json, StringComparison.Ordinal);
    }

    private sealed class StubRailWithRenderers : IBowireRailContribution
    {
        public string Id => "renderer-keys";
        public string DisplayName => "Renderer Keys";
        public string IconKey => "square";
        public int SortIndex => 100;
        public string Group => "work";
        public string SidebarKind => "none";
        public string? SidebarRendererKey => "rendererKeysSidebar";
        public string? MainPaneRendererKey => "rendererKeysMain";
    }

    [Fact]
    public void ToJson_Omits_Renderer_Keys_When_Unset()
    {
        // #314 — keys default to null. Emitted only when set so the
        // JSON stays terse for rails relying on core's hardcoded
        // dispatcher arm. Old shape stays byte-stable for those.
        var registry = new BowireRailRegistry();
        registry.Register(new StubRail("legacy"));

        var json = registry.ToJson();
        Assert.DoesNotContain("sidebarRendererKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mainPaneRendererKey", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_Carries_Renderer_Keys_When_Set()
    {
        // #314 — when a rail descriptor sets SidebarRendererKey /
        // MainPaneRendererKey, both must round-trip into the JSON
        // seed so render-sidebar.js / render-main.js can dispatch
        // through window.__bowireRailRenderers.
        var registry = new BowireRailRegistry();
        registry.Register(new StubRailWithRenderers());

        var json = registry.ToJson();
        Assert.Contains("\"sidebarRendererKey\":\"rendererKeysSidebar\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mainPaneRendererKey\":\"rendererKeysMain\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_Picks_Up_BuiltIn_Rails_From_Loaded_Assembly()
    {
        // Force each always-on rail's assembly into the AppDomain so the
        // discovery scan picks them up. After #306 Phase G each rail lived
        // in its own Kuestenlogik.Bowire.Rail.* assembly, so touching just
        // Home wouldn't pull Discover / Compose / Workspaces with it the
        // way it did when every descriptor sat in core's BuiltInRails.cs.
        // v2.1 (#325): Home + Discover folded back into Core under the
        // Kuestenlogik.Bowire.Rails namespace — descriptor-only rails
        // didn't earn their own NuGet. Compose + Workspaces still live in
        // their own packages so the touch keeps the discovery scan honest.
        _ = new BowireHomeRailContribution().Id;
        _ = new BowireDiscoverRailContribution().Id;
        _ = new BowireComposeRailContribution().Id;
        _ = new BowireWorkspacesRailContribution().Id;

        var registry = BowireRailRegistry.Discover();
        // Every always-on rail must show up — this is the invariant the
        // JS bundle relies on. Phase B's ALWAYS_ON_RAIL_MODES fallback
        // would mask a registry gap; this test catches it directly.
        Assert.NotNull(registry.GetById("home"));
        Assert.NotNull(registry.GetById("discover"));
        Assert.NotNull(registry.GetById("compose"));
        Assert.NotNull(registry.GetById("workspaces"));
    }

    [Fact]
    public void ModuleRegistry_Register_And_Lookup_Round_Trip()
    {
        var registry = new BowireModuleRegistry();
        registry.Register(new StubModule("ai"));
        registry.Register(new StubModule("assistant"));

        Assert.Equal(2, registry.Modules.Count);
        Assert.NotNull(registry.GetById("ai"));
        Assert.Null(registry.GetById("missing"));
    }

    [Fact]
    public void ModuleRegistry_ToJson_Emits_DefaultEnabled_Flag()
    {
        var registry = new BowireModuleRegistry();
        registry.Register(new StubModule("ai"));

        var json = registry.ToJson();
        Assert.Contains("\"id\":\"ai\"", json, StringComparison.Ordinal);
        Assert.Contains("\"defaultEnabled\":true", json, StringComparison.Ordinal);
        // #310 — description field added so the Settings → Modules UI
        // can render a one-line copy under each label. Empty by
        // default; the field MUST still be emitted so the JS-side
        // descriptor lookup hits a known key shape.
        Assert.Contains("\"description\":\"\"", json, StringComparison.Ordinal);
    }

    private sealed class StubModuleWithDescription(string id, string description)
        : IBowireModuleContribution
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public string Description { get; } = description;
    }

    [Fact]
    public void ModuleRegistry_ToJson_Emits_Description()
    {
        var registry = new BowireModuleRegistry();
        registry.Register(new StubModuleWithDescription("ai", "Chat panel and hints."));

        var json = registry.ToJson();
        Assert.Contains("\"description\":\"Chat panel and hints.\"", json, StringComparison.Ordinal);
    }
}

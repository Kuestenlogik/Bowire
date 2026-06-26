// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

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
    private sealed class StubRail(string id, int sort = 100, bool alwaysOn = false, bool hideFromRail = false) : IBowireRailContribution
    {
        public string Id { get; } = id;
        public string DisplayName => $"{Id[..1].ToUpperInvariant()}{Id[1..]}";
        public string IconKey => "square";
        public int SortIndex { get; } = sort;
        public string Group => "work";
        public string SidebarKind => "none";
        public bool AlwaysOn { get; } = alwaysOn;
        public bool HideFromRail { get; } = hideFromRail;
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
    public void Discover_Picks_Up_BuiltIn_Rails_From_Loaded_Assembly()
    {
        // Force the Plugins namespace types to load by touching one.
        _ = new BowireHomeRailContribution().Id;

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
    }
}

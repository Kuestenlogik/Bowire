// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Agent.Tests;

/// <summary>
/// What the Settings → Catalogue providers dialog sends for this package,
/// and whether it arrives (#309).
/// </summary>
/// <remarks>
/// Core does not reference this package, so an override is matched onto
/// <see cref="BowireAgentCatalogueOptions"/> by reflection — by assembly
/// name prefix, by provider id, by options class name, by property name and
/// by constructor shape. Nothing checks any of those couplings, and a miss
/// falls back to the parameterless constructor, which loads the provider on
/// its own defaults and discards what the operator typed. The same fallback
/// was silently swallowing the Kubernetes package's override.
/// </remarks>
public sealed class AgentCatalogueOverrideSeamTests
{
    public AgentCatalogueOverrideSeamTests()
        // The seam scans AppDomain.CurrentDomain.GetAssemblies(), and a
        // referenced assembly is not loaded until something touches it.
        => _ = typeof(AgentCatalogueProvider);

    [Theory]
    [InlineData("agent")]
    [InlineData("Agent")]
    public void The_Provider_Is_Found_By_Its_Id(string id)
    {
        var provider = BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = id });

        Assert.IsType<AgentCatalogueProvider>(provider);
    }

    [Fact]
    public void Selected_Without_Options_It_Still_Loads()
    {
        var provider = BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = "agent", Agent = null });

        Assert.IsType<AgentCatalogueProvider>(provider);
    }

    [Fact]
    public async Task What_The_Operator_Typed_Reaches_The_Provider()
    {
        // Through the provider's own behaviour rather than its private
        // state: the stub response is parsed only if the override value
        // landed on the options object the provider resolves.
        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride
        {
            Provider = "agent",
            Agent = new BowireAgentCatalogueOverrideOptions
            {
                StubResponse = """
                {"version":1,"agents":[{"agentId":"harbor","serviceName":"berths",
                  "entries":[{"url":"https://berths.internal","name":"berths"}]}]}
                """,
            },
        });

        var entries = await provider!.FetchAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.Equal("berths", entry.Name);
        Assert.Equal("https://berths.internal", entry.Url);
        // The agent id is prefixed onto the tags so the workbench can filter
        // to one agent; it is only there if the whole document was read
        // through the override, not just the url.
        Assert.Contains("agent:harbor", entry.Tags!);
    }

    [Fact]
    public async Task A_Field_Left_Blank_Keeps_The_Provider_Default()
    {
        // The dialog posts every field of its form, most of them empty. An
        // empty hub URL means "unset" — were it taken literally, the
        // provider would try to fetch from it instead of staying idle.
        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride
        {
            Provider = "agent",
            Agent = new BowireAgentCatalogueOverrideOptions
            {
                HubUrl = "",
                BootstrapToken = null,
            },
        });

        var entries = await provider!.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }
}

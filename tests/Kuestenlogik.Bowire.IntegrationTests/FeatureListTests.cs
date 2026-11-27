// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// #1065 — the page learns which optional packages a host serves from
/// <c>__BOWIRE_CONFIG__.features</c>, so it never probes one that is absent
/// and logs a 404 on every start.
/// </summary>
public sealed class FeatureListTests : IClassFixture<BowireTestFixture>
{
    private readonly HttpClient _client;

    public FeatureListTests(BowireTestFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task A_Mapped_Contribution_Is_Listed_And_One_Without_An_Id_Is_Not()
    {
        var html = await _client.GetStringAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);

        var line = html.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("features:", StringComparison.Ordinal));
        Assert.Contains("\"feature-list-test\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("null", line, StringComparison.Ordinal);
    }
}

/// <summary>Discovered like any package's contribution: concrete, parameterless, in a Bowire assembly.</summary>
internal sealed class FeatureListTestContribution : IBowireEndpointContribution
{
    public string? FeatureId => "feature-list-test";

    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath) { }
}

/// <summary>A contribution with no feature id adds nothing to the list.</summary>
internal sealed class FeatureListAnonymousContribution : IBowireEndpointContribution
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath) { }
}

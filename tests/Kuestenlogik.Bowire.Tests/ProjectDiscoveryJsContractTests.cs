// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #172 — JS-side contract pins for the <c>.bowire/project.json</c> boot
/// probe. Same substring-over-source approach as
/// <see cref="EmbeddedZeroConfigJsContractTests"/>: the concatenated
/// <c>bowire.js</c> has no JS test runner, so pinning the probe's wiring
/// is the cheapest way to fail loudly if the boot contract drifts (the
/// probe only manifests in a browser, and a regression is silent — the
/// operator just never learns the repo's manifest was picked up).
/// </summary>
public sealed class ProjectDiscoveryJsContractTests
{
    private static readonly Lazy<string> CoreBundle = new(LoadCoreBundle);

    [Fact]
    public void Boot_Probes_The_Project_Endpoint()
    {
        var bundle = CoreBundle.Value;
        Assert.Contains("/api/project", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Gates_On_Found_And_Is_A_No_Op_Otherwise()
    {
        // The 404 / no-manifest path must degrade silently: the handler
        // returns early unless the payload reports found === true, mirroring
        // the sibling capability probes.
        var bundle = CoreBundle.Value;
        Assert.Contains("if (!data || !data.found) return;", bundle, StringComparison.Ordinal);
        Assert.Contains("discoveredProject = data;", bundle, StringComparison.Ordinal);
    }

    private static string LoadCoreBundle()
    {
        var assembly = typeof(global::Kuestenlogik.Bowire.BowireServiceCollectionExtensions).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.wwwroot.bowire.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The JS concat target may have failed; try `dotnet build`.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

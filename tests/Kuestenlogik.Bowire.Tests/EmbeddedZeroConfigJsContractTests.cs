// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #535 — JS-side contract pins for the zero-config embedded first run.
/// Same regex/substring-over-source approach as
/// <see cref="InterceptRailJsContractTests"/>: Bowire has no JS test
/// runner for the core bundle, so structural invariants over the
/// concatenated <c>bowire.js</c> are the cheapest way to fail loudly
/// when the boot contract drifts.
///
/// What is worth pinning here is precisely the wiring that has no other
/// automated coverage: the three-layer auto-create resolver, the
/// mode-derived landing rail, and the host-derived workspace name. All
/// three only manifest in a browser, and a regression is silent — the
/// operator just lands somewhere slightly worse.
/// </summary>
public sealed class EmbeddedZeroConfigJsContractTests
{
    private static readonly Lazy<string> CoreBundle = new(LoadCoreBundle);

    [Fact]
    public void Auto_Create_Resolver_Exists_And_Reports_Its_Source()
    {
        // One resolver, called by both the boot seed and the Settings
        // row. The `source` discriminator is what stops Settings from
        // labelling a mode default as "forced by the host" — without it
        // the row would render read-only in every embedded install.
        var bundle = CoreBundle.Value;
        Assert.Contains("function resolveAutoCreateInitialWorkspace(", bundle, StringComparison.Ordinal);
        Assert.Contains("source: 'host'", bundle, StringComparison.Ordinal);
        Assert.Contains("source: 'browser'", bundle, StringComparison.Ordinal);
        Assert.Contains("source: 'mode-default'", bundle, StringComparison.Ordinal);
        // Settings → General consumes the same resolver rather than
        // re-reading localStorage on its own.
        Assert.Contains("resolveAutoCreateInitialWorkspace()", bundle, StringComparison.Ordinal);
        Assert.Contains("_autoCreate.source === 'host'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_Create_Resolver_Reads_The_Host_Flag_As_A_Tri_State()
    {
        // `typeof … === 'boolean'` is the whole point: config.
        // autoCreateInitialWorkspace is null when the host has no
        // stance, and a `=== true` comparison would collapse that into
        // "not forced" while an `=== false` one would collapse an unset
        // option into an explicit opt-out.
        var bundle = CoreBundle.Value;
        Assert.Contains("typeof config.autoCreateInitialWorkspace === 'boolean'", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("config.autoCreateInitialWorkspace === true", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Landing_Rail_Default_Is_Mode_Derived()
    {
        // Embedded lands on Discover (the host's own API is already
        // discovered before first paint); standalone keeps Home, where
        // the "Create your first workspace" CTA lives. The persisted
        // bowire_rail_mode still wins whenever it is set.
        var bundle = CoreBundle.Value;
        Assert.Contains("(uiMode === 'embedded') ? 'discover' : 'home'", bundle, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem('bowire_rail_mode') || _defaultRailMode", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Seed_Names_The_Workspace_After_The_Host_And_Gates_On_Never_Seeded()
    {
        // config.hostName comes from BowireHtmlGenerator's
        // ResolveHostDisplayName (Title → entry assembly → origin).
        // The `rawWs == null` gate is what makes a deliberate delete
        // stick: deleteWorkspace() persists `[]`, so the key exists from
        // then on and an empty-list check alone would resurrect it.
        var bundle = CoreBundle.Value;
        Assert.Contains("config.hostName", bundle, StringComparison.Ordinal);
        Assert.Contains("rawWs == null && autoCreateInitial", bundle, StringComparison.Ordinal);
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

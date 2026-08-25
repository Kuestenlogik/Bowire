// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The UI-driven catalogue-provider override (#309) and the DTO→provider
/// mapping the CLI shares with it (#537).
/// </summary>
/// <remarks>
/// <para>
/// Two things are worth pinning down. <c>BuildProvider</c> is deliberately
/// internal-not-private so that <c>bowire catalogue list --provider local
/// --path …</c> constructs its one-shot provider through the same code as a
/// persisted override — the whole point being that there is no second
/// mapping to drift. And the store has to survive a bad file: it runs during
/// construction, before anything can catch for it, so a hand-edited or
/// truncated config must degrade to "no override" rather than take the host
/// down on startup.
/// </para>
/// <para>
/// Joins the serialised collection because these drive
/// <c>BOWIRE_CATALOGUE_CONFIG_PATH</c>, which is process-global.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class BowireCatalogueOverrideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bowire-cat-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnv = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH");

    private string ConfigPath => Path.Combine(_dir, "catalogue-config.json");

    private void PointAtTemp()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", ConfigPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", _previousEnv);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- DTO → provider ----

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("Local")]
    public void A_Provider_Id_Is_Matched_Whatever_Casing_The_Operator_Typed(string id)
    {
        // The wire shape documents lower case, but the id also arrives from a
        // hand-typed --provider flag.
        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = id });
        Assert.IsType<LocalCatalogueProvider>(provider);
    }

    [Fact]
    public void Each_Built_In_Provider_Id_Maps_To_Its_Provider()
    {
        Assert.IsType<LocalCatalogueProvider>(
            BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = "local" }));
        Assert.IsType<HttpCatalogueProvider>(
            BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = "http" }));
        Assert.IsType<ConsulCatalogueProvider>(
            BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = "consul" }));
    }

    [Fact]
    public void A_Provider_Id_Nothing_Answers_To_Yields_No_Provider()
    {
        // Including the sibling-package ids when their assembly is not
        // loaded: null here means "fall back to appsettings", which is the
        // right outcome for a package that was never installed.
        Assert.Null(BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = "nonsense" }));
        Assert.Null(BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = "" }));
        Assert.Null(BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride { Provider = null }));
    }

    [Fact]
    public void A_Provider_Selected_Without_Options_Still_Builds()
    {
        // The Settings UI can post a provider id before the operator has
        // filled anything in. Each provider has a usable default — local
        // resolves ~/.bowire/catalogue.json — so this must not throw.
        Assert.NotNull(BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = "local", Local = null }));
        Assert.NotNull(BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = "http", Http = null }));
        Assert.NotNull(BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = "consul", Consul = null }));
    }

    // ---- persistence ----

    [Fact]
    public void Save_Persists_The_Override_And_Applies_It()
    {
        PointAtTemp();
        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);

        store.Save(new BowireCatalogueOverride
        {
            Provider = "local",
            Local = new BowireLocalCatalogueOptions { Path = "/tmp/catalogue.json" },
        });

        Assert.True(File.Exists(ConfigPath));
        Assert.True(accessor.HasOverride);
        Assert.Equal("local", store.Current?.Provider);
        Assert.Equal("/tmp/catalogue.json", store.Current?.Local?.Path);
    }

    [Fact]
    public void A_Persisted_Override_Is_Re_Applied_By_The_Next_Process()
    {
        // The reason it is written at all: an operator who picked a provider
        // in Settings should still have it after a restart.
        PointAtTemp();
        new BowireCatalogueOverrideStore(new BowireCatalogueProviderAccessor(null))
            .Save(new BowireCatalogueOverride { Provider = "consul" });

        var accessor = new BowireCatalogueProviderAccessor(null);
        var reloaded = new BowireCatalogueOverrideStore(accessor);

        Assert.Equal("consul", reloaded.Current?.Provider);
        Assert.True(accessor.HasOverride);
        Assert.IsType<ConsulCatalogueProvider>(accessor.Provider);
    }

    [Fact]
    public void Clear_Removes_The_File_And_Restores_The_Appsettings_Fallback()
    {
        PointAtTemp();
        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);
        store.Save(new BowireCatalogueOverride { Provider = "local" });

        store.Clear();

        Assert.False(File.Exists(ConfigPath));
        Assert.Null(store.Current);
        Assert.False(accessor.HasOverride);
    }

    [Fact]
    public void An_Override_With_No_Provider_Clears_Rather_Than_Building_Nothing()
    {
        // How the UI says "go back to appsettings" without a separate call.
        PointAtTemp();
        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);
        store.Save(new BowireCatalogueOverride { Provider = "local" });

        store.Save(new BowireCatalogueOverride { Provider = "" });

        Assert.False(accessor.HasOverride);
    }

    [Fact]
    public void A_Corrupt_Config_Degrades_To_No_Override_Instead_Of_Failing_Startup()
    {
        // Load() runs inside the constructor, so there is no caller in a
        // position to handle this — a hand-edited or truncated file has to
        // read as "no override", not as a host that will not start.
        PointAtTemp();
        File.WriteAllText(ConfigPath, "{ this is not json");

        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);

        Assert.Null(store.Current);
        Assert.False(accessor.HasOverride);
    }

    [Fact]
    public void An_Empty_Config_Is_Treated_As_No_Override()
    {
        PointAtTemp();
        File.WriteAllText(ConfigPath, "   ");

        Assert.Null(new BowireCatalogueOverrideStore(new BowireCatalogueProviderAccessor(null)).Current);
    }

    [Fact]
    public void Save_Rejects_A_Null_Payload()
    {
        PointAtTemp();
        var store = new BowireCatalogueOverrideStore(new BowireCatalogueProviderAccessor(null));
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    [Fact]
    public void The_Store_Needs_An_Accessor()
        => Assert.Throws<ArgumentNullException>(() => new BowireCatalogueOverrideStore(null!));

    [Fact]
    public void The_Persisted_Document_Omits_The_Sections_That_Were_Not_Filled_In()
    {
        // Otherwise every saved config carries four null provider blocks, and
        // a file someone opens to hand-edit is mostly noise.
        PointAtTemp();
        new BowireCatalogueOverrideStore(new BowireCatalogueProviderAccessor(null))
            .Save(new BowireCatalogueOverride
            {
                Provider = "http",
                Http = new BowireHttpCatalogueOptions { Url = "https://catalogue.example.com/index.json" },
            });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal("http", doc.RootElement.GetProperty("provider").GetString());
        Assert.True(doc.RootElement.TryGetProperty("http", out _));
        Assert.False(doc.RootElement.TryGetProperty("local", out _));
        Assert.False(doc.RootElement.TryGetProperty("consul", out _));
        Assert.False(doc.RootElement.TryGetProperty("kubernetes", out _));
    }

    [Fact]
    public void ResolvePath_Prefers_The_Environment_Override()
    {
        PointAtTemp();
        Assert.Equal(ConfigPath, BowireCatalogueOverrideStore.ResolvePath());
    }

    [Fact]
    public void ResolvePath_Falls_Back_To_The_User_Profile()
    {
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", null);

        var path = BowireCatalogueOverrideStore.ResolvePath();

        // Empty only when the platform reports no user profile at all.
        if (path.Length > 0)
        {
            Assert.EndsWith("catalogue-config.json", path, StringComparison.Ordinal);
            Assert.Contains(".bowire", path, StringComparison.Ordinal);
        }
    }
}

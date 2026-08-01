// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;
using Kuestenlogik.Bowire.PluginLoading;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// The acceptance criteria of #546, as tests.
/// </summary>
/// <remarks>
/// The ticket asks for three things: a test can construct plugin
/// management with an explicit directory and no environment variable, two
/// instances can coexist with different plugin sets, and the duplicate
/// ledger is gone. The first two live here; the third is
/// <see cref="NoStaticPluginStateTests"/>, because "gone" has to mean
/// something a rename cannot satisfy.
/// </remarks>
public sealed class BowirePluginLoaderTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string NewDir(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    // A real Bowire plugin assembly that ships next to the test runner.
    // OData is the smallest with self-contained dependencies, so renaming
    // a copy of it is the cheapest way to make a loadable stub plugin.
    private static string ProbePluginDll => Path.Combine(
        Path.GetDirectoryName(typeof(BowirePluginLoaderTests).Assembly.Location)!,
        "Kuestenlogik.Bowire.Protocol.OData.dll");

    private static void SeedPlugin(string root, string packageId)
    {
        var sub = Path.Combine(root, packageId);
        Directory.CreateDirectory(sub);
        File.Copy(ProbePluginDll, Path.Combine(sub, packageId + ".dll"));
    }

    [Fact]
    public void Load_ExplicitDirectory_IgnoresPoisonedEnvironmentVariable()
    {
        // Acceptance criterion 1: plugin management constructed with an
        // explicit directory, reading nothing ambient. The environment
        // variable points somewhere else entirely and must not matter.
        var wanted = NewDir("bowire-loader-explicit-");
        var poison = NewDir("bowire-loader-poison-");
        SeedPlugin(wanted, "Explicit.Wanted");
        SeedPlugin(poison, "Poison.Unwanted");

        var previous = Environment.GetEnvironmentVariable(BowirePluginOptions.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, poison);

            var loader = new BowirePluginLoader(new BowirePluginOptions { PluginDirectory = wanted });
            var results = loader.Load();

            Assert.Contains(results, r => r.PackageId == "Explicit.Wanted");
            Assert.DoesNotContain(results, r => r.PackageId == "Poison.Unwanted");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, previous);
        }
    }

    [Fact]
    public void TwoLoaders_DifferentDirectories_HaveDisjointPluginSets()
    {
        // Acceptance criterion 2. Before #546 the ledger was a single
        // static set, so whichever instance loaded second was told the
        // other's plugins were already loaded and ended up with a view it
        // never asked for.
        //
        // What this does NOT claim: that the two are isolated at the
        // assembly level. Loading is process-wide, so both assemblies stay
        // visible through AppDomain.CurrentDomain.GetAssemblies() once
        // either loader has run. What differs is what each loader owns
        // and reports, which is the part the ticket can fix.
        var rootA = NewDir("bowire-loader-a-");
        var rootB = NewDir("bowire-loader-b-");
        SeedPlugin(rootA, "Alpha.Plug");
        SeedPlugin(rootB, "Beta.Plug");

        var a = new BowirePluginLoader(new BowirePluginOptions { PluginDirectory = rootA });
        var b = new BowirePluginLoader(new BowirePluginOptions { PluginDirectory = rootB });

        var loadedA = a.Load().Where(r => r.Status == PluginLoadStatus.Loaded).Select(r => r.PackageId).ToList();
        var loadedB = b.Load().Where(r => r.Status == PluginLoadStatus.Loaded).Select(r => r.PackageId).ToList();

        Assert.Equal(["Alpha.Plug"], loadedA);
        Assert.Equal(["Beta.Plug"], loadedB);
    }

    [Fact]
    public void TwoLoaders_SameDirectory_BothLoadIndependently()
    {
        // The flip side of the same criterion: two instances over one
        // directory do NOT share a ledger, so the second is not told the
        // first's work was already done. That used to be impossible.
        var root = NewDir("bowire-loader-shared-");
        SeedPlugin(root, "Shared.Plug");

        var first = new BowirePluginLoader(new BowirePluginOptions { PluginDirectory = root });
        var second = new BowirePluginLoader(new BowirePluginOptions { PluginDirectory = root });

        var firstEntry = Assert.Single(first.Load(), r => r.PackageId == "Shared.Plug");
        var secondEntry = Assert.Single(second.Load(), r => r.PackageId == "Shared.Plug");

        Assert.Equal(PluginLoadStatus.Loaded, firstEntry.Status);
        Assert.Equal(PluginLoadStatus.Loaded, secondEntry.Status);
    }

    [Fact]
    public void Load_MissingDirectory_PublishesEmptyResults()
    {
        var loader = TestPluginLoaders.None();

        var results = loader.Load();

        Assert.Empty(results);
        Assert.Same(results, loader.LastResults);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => new BowirePluginLoader((BowirePluginOptions)null!));

    [Fact]
    public void LastResults_BeforeFirstLoad_IsEmpty()
        => Assert.Empty(TestPluginLoaders.None().LastResults);
}

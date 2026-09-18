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
[Collection("PluginLoadResults")]
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

    // Real Bowire plugin assemblies that ship next to the test runner.
    // Each is small and self-contained, so a renamed copy is the cheapest
    // loadable stub plugin.
    //
    // Two of them, and which one a test uses matters. Copying a file does
    // not change the assembly identity inside it, so seeding two packages
    // from ONE source puts two copies of one identity in the process --
    // and this source ships beside the test runner, so the default context
    // may already hold a third. Which copy a resolve finds then depends on
    // the order the tests happened to run in, which is how this suite
    // produced a failure that came and went with unrelated edits.
    private static string ProbeDll(string fileName) => Path.Combine(
        Path.GetDirectoryName(typeof(BowirePluginLoaderTests).Assembly.Location)!,
        fileName);

    private const string ProbeA = "Kuestenlogik.Bowire.Protocol.OData.dll";
    private const string ProbeB = "Kuestenlogik.Bowire.Protocol.JsonRpc.dll";

    private static void SeedPlugin(string root, string packageId, string source = ProbeA)
    {
        var sub = Path.Combine(root, packageId);
        Directory.CreateDirectory(sub);
        File.Copy(ProbeDll(source), Path.Combine(sub, packageId + ".dll"));
    }

    [Fact]
    public void Load_ExplicitDirectory_IgnoresPoisonedEnvironmentVariable()
    {
        // Acceptance criterion 1: plugin management constructed with an
        // explicit directory, reading nothing ambient. The environment
        // variable points somewhere else entirely and must not matter.
        var wanted = NewDir("bowire-loader-explicit-");
        var poison = NewDir("bowire-loader-poison-");
        SeedPlugin(wanted, "Explicit.Wanted", ProbeA);
        SeedPlugin(poison, "Poison.Unwanted", ProbeB);

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
        // Distinct source assemblies: two packages that are really two
        // assemblies, which is what the claim below is about.
        SeedPlugin(rootA, "Alpha.Plug", ProbeA);
        SeedPlugin(rootB, "Beta.Plug", ProbeB);

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

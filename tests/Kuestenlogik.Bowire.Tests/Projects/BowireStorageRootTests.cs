// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Projects;

/// <summary>
/// #591 — where a Bowire instance keeps its collections, environments,
/// recordings and presets.
/// <para>
/// The behaviour these pin is mostly the behaviour that must NOT change:
/// anyone without a manifest, and anyone whose manifest says nothing about
/// storage, keeps the machine-wide <c>~/.bowire/</c> they have today. Rooting
/// storage at a project the moment any manifest exists would have silently
/// relocated the data of everyone already using <c>project.json</c> for
/// sources and rules, and their collections would have appeared to vanish.
/// </para>
/// </summary>
public sealed class BowireStorageRootTests : IDisposable
{
    private readonly string _dir;

    public BowireStorageRootTests()
    {
        _dir = SafePath.Combine(Path.GetTempPath(), "bowire-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteManifest(string json, string? subdir = null)
    {
        var projectRoot = subdir is null ? _dir : SafePath.Combine(_dir, subdir);
        var conventionDir = Path.Combine(projectRoot, BowireProjectLoader.ConventionDirName);
        Directory.CreateDirectory(conventionDir);
        File.WriteAllText(Path.Combine(conventionDir, BowireProjectLoader.ConventionFileName), json);
        return projectRoot;
    }

    private static string ProjectStore(string projectRoot) =>
        Path.Combine(projectRoot, BowireProjectLoader.ConventionDirName);

    [Fact]
    public void NoManifest_ResolvesToTheUserProfile()
    {
        Assert.Equal(DefaultBowireUserStore.UserProfileRoot, BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void ManifestWithoutStorageKey_KeepsTheUserProfile()
    {
        // The migration-safety case: an existing manifest that only declares
        // sources must not move anybody's data.
        WriteManifest("""
            { "version": 1, "name": "x", "sources": [ { "url": "http://localhost:1" } ] }
            """);

        Assert.Equal(DefaultBowireUserStore.UserProfileRoot, BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void ExplicitUser_KeepsTheUserProfile()
    {
        WriteManifest("""{ "version": 1, "name": "x", "storage": "user" }""");

        Assert.Equal(DefaultBowireUserStore.UserProfileRoot, BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void StorageProject_RootsAtTheProject()
    {
        var root = WriteManifest("""{ "version": 1, "name": "x", "storage": "project" }""");

        Assert.Equal(ProjectStore(root), BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void StorageProject_IsCaseInsensitive()
    {
        // Hand-edited JSON; "Project" must not silently mean the default.
        var root = WriteManifest("""{ "version": 1, "name": "x", "storage": "Project" }""");

        Assert.Equal(ProjectStore(root), BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void ResolvesFromASubdirectory_BecauseDiscoveryWalksUp()
    {
        // This is what makes the answer a property of the repo rather than of
        // the launcher: the CLI started three levels down, an IDE extension
        // started at the workspace root, and a CI job started at the checkout
        // all land on the same store.
        var root = WriteManifest("""{ "version": 1, "name": "x", "storage": "project" }""");
        var deep = SafePath.Combine(root, "src/inner/deeper");
        Directory.CreateDirectory(deep);

        Assert.Equal(ProjectStore(root), BowireStorageRoot.Resolve(deep));
    }

    [Fact]
    public void UnreadableManifest_FallsBackRatherThanTakingStorageDown()
    {
        // A broken manifest is reported by `bowire project validate`, loudly.
        // It must not also make the instance unusable — an operator fixing a
        // JSON typo should still be able to open the workbench.
        WriteManifest("{ this is not json");

        Assert.Equal(DefaultBowireUserStore.UserProfileRoot, BowireStorageRoot.Resolve(_dir));
    }

    [Fact]
    public void TwoProjects_ResolveToDifferentStores()
    {
        // The headline symptom this issue exists for: two repos open at once
        // used to share one set of collections.
        var a = WriteManifest("""{ "version": 1, "name": "a", "storage": "project" }""", "repo-a");
        var b = WriteManifest("""{ "version": 1, "name": "b", "storage": "project" }""", "repo-b");

        Assert.NotEqual(BowireStorageRoot.Resolve(a), BowireStorageRoot.Resolve(b));
        Assert.Equal(ProjectStore(a), BowireStorageRoot.Resolve(a));
        Assert.Equal(ProjectStore(b), BowireStorageRoot.Resolve(b));
    }

    [Fact]
    public void UnknownStorageValue_IsAValidationError()
    {
        // Silently falling back would leave the operator believing their
        // collections live in the repo while they are somewhere else.
        var manifest = BowireProjectFile.Parse("""{ "version": 1, "storage": "repo" }""");

        var errors = manifest.Validate();

        Assert.Contains(errors, e => e.Contains("storage:", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownStorageValues_PassValidation()
    {
        foreach (var value in new[] { "project", "user", "PROJECT" })
        {
            var manifest = BowireProjectFile.Parse($$"""{ "version": 1, "storage": "{{value}}" }""");
            Assert.DoesNotContain(manifest.Validate(), e => e.Contains("storage:", StringComparison.Ordinal));
        }
    }
}

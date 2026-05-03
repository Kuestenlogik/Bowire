// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.Loader;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Guards the plugin-isolation contract: shared prefixes delegate to
/// the default ALC (type identity preserved), plugin-private deps get
/// loaded from the plugin's own directory, and each plugin gets its
/// own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </summary>
public sealed class BowirePluginLoadContextTests : IDisposable
{
    private readonly string _tempDir;

    public BowirePluginLoadContextTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-alc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptyPluginDir()
    {
        Assert.Throws<ArgumentException>(() => new BowirePluginLoadContext(""));
        Assert.Throws<ArgumentNullException>(() => new BowirePluginLoadContext(null!));
    }

    [Fact]
    public void ContextName_IncludesPluginDirectoryLeaf()
    {
        var pluginDir = Path.Combine(_tempDir, "MyCompany.Plugin");
        Directory.CreateDirectory(pluginDir);

        var ctx = new BowirePluginLoadContext(pluginDir);

        Assert.Contains("MyCompany.Plugin", ctx.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void IsShared_KLBowirePrefix_True()
    {
        var ctx = new BowirePluginLoadContext(_tempDir);

        Assert.True(ctx.IsShared("Kuestenlogik.Bowire"));
        Assert.True(ctx.IsShared("Kuestenlogik.Bowire.Protocol.Grpc"));
    }

    [Fact]
    public void IsShared_FrameworkPrefixes_True()
    {
        var ctx = new BowirePluginLoadContext(_tempDir);

        Assert.True(ctx.IsShared("System.Text.Json"));
        Assert.True(ctx.IsShared("Microsoft.AspNetCore.Http"));
        Assert.True(ctx.IsShared("NETStandard.Library"));
    }

    [Fact]
    public void IsShared_ThirdPartyName_False()
    {
        var ctx = new BowirePluginLoadContext(_tempDir);

        Assert.False(ctx.IsShared("MQTTnet"));
        Assert.False(ctx.IsShared("Serilog"));
        Assert.False(ctx.IsShared("Newtonsoft.Json"));
    }

    [Fact]
    public void IsShared_NullOrEmpty_False()
    {
        var ctx = new BowirePluginLoadContext(_tempDir);

        Assert.False(ctx.IsShared(null));
        Assert.False(ctx.IsShared(""));
    }

    private static readonly string[] s_extraPrefixes = ["MyCorp."];

    [Fact]
    public void IsShared_AdditionalPrefixes_Honored()
    {
        var ctx = new BowirePluginLoadContext(_tempDir,
            additionalSharedPrefixes: s_extraPrefixes);

        Assert.True(ctx.IsShared("MyCorp.SharedSdk"));
        Assert.True(ctx.IsShared("Kuestenlogik.Bowire")); // defaults kept
        Assert.False(ctx.IsShared("OtherCorp.Thing"));
    }

    [Fact]
    public void PluginDirectory_AbsolutePath_Preserved()
    {
        var pluginDir = Path.Combine(_tempDir, "absdir");
        Directory.CreateDirectory(pluginDir);

        var ctx = new BowirePluginLoadContext(pluginDir);

        Assert.Equal(Path.GetFullPath(pluginDir), ctx.PluginDirectory);
    }

    [Fact]
    public void LoadFromAssemblyPath_PutsPluginAssemblyInContext()
    {
        // We use the test assembly itself as a "fake plugin" — it's
        // already on disk, it's not in the shared prefix list (it's
        // named "Kuestenlogik.Bowire.Tests" — wait, that IS shared). Use the
        // xunit runtime assembly instead.
        var xunitDll = typeof(FactAttribute).Assembly.Location;
        var stagingDir = Path.Combine(_tempDir, "stage");
        Directory.CreateDirectory(stagingDir);

        var stagedPath = Path.Combine(stagingDir, Path.GetFileName(xunitDll));
        File.Copy(xunitDll, stagedPath);

        var ctx = new BowirePluginLoadContext(stagingDir);
        var loaded = ctx.LoadFromAssemblyPath(stagedPath);

        Assert.NotNull(loaded);
        Assert.Same(ctx, AssemblyLoadContext.GetLoadContext(loaded));
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded));
    }

    [Fact]
    public void SharedPrefixAssembly_ResolvedFromDefault_NotFromPluginDir()
    {
        // Verify the delegation: when a plugin asks for a shared
        // assembly, the context falls back to the default ALC instead
        // of loading a copy from the plugin folder.
        //
        // Technique: manually invoke the protected Load() hook via
        // reflection with a Kuestenlogik.Bowire-prefixed AssemblyName. A null
        // return value means "delegate to default" in the ALC
        // contract, which is exactly what we want.
        var ctx = new BowirePluginLoadContext(_tempDir);
        var loadMethod = typeof(BowirePluginLoadContext).GetMethod(
            "Load", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loadMethod);

        var result = loadMethod!.Invoke(ctx, [new AssemblyName("Kuestenlogik.Bowire")]);

        Assert.Null(result);
    }
}

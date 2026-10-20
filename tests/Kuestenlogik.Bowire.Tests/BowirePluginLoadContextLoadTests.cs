// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Targeted tests for the plugin-private branch of
/// <see cref="BowirePluginLoadContext.Load(AssemblyName)"/> — the path
/// the runtime takes when an assembly name is NOT in the shared-prefix
/// list. The shared-prefix delegation is covered in
/// <see cref="BowirePluginLoadContextTests"/>; this class fills in the
/// filename-resolution branch (look for the DLL in the plugin folder)
/// via reflection.
/// </summary>
public sealed class BowirePluginLoadContextLoadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "bowire-plc-load-" + Guid.NewGuid().ToString("N"));

    public BowirePluginLoadContextLoadTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        // Best-effort: an assembly loaded from the folder stays mapped until
        // its collectible context is actually collected, which is not now.
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static MethodInfo GetLoadMethod() =>
        typeof(BowirePluginLoadContext).GetMethod(
            "Load", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Load_PluginPrivate_Name_With_Missing_Dll_Returns_Null()
    {
        // Non-shared assembly name + no matching DLL in the plugin
        // folder → Load returns null and lets the runtime raise the
        // standard "couldn't resolve" exception.
        var ctx = new BowirePluginLoadContext(_tempDir);
        var loadMethod = GetLoadMethod();

        var result = loadMethod.Invoke(ctx, [new AssemblyName("PluginPrivate.Missing")]);

        Assert.Null(result);
    }

    [Fact]
    public void Load_PluginPrivate_Name_With_Empty_Name_Returns_Null()
    {
        // The IsNullOrEmpty guard for assemblyName.Name on the
        // plugin-private branch — protects against an AssemblyName
        // built without a Name (rare but possible via reflection).
        var ctx = new BowirePluginLoadContext(_tempDir);
        var loadMethod = GetLoadMethod();
        var nameWithoutSimpleName = new AssemblyName();

        var result = loadMethod.Invoke(ctx, [nameWithoutSimpleName]);

        Assert.Null(result);
    }

    [Fact]
    public void A_Shared_Prefix_Assembly_The_Host_Lacks_Loads_From_The_Plugin_Folder()
    {
        // #817 — Microsoft.Identity.Web, the OIDC provider's dependency, is
        // a Microsoft.* name the host does not ship. Delegating it to the
        // default ALC only failed; it is plugin-private like any other.
        const string name = "Microsoft.Bowire.PluginOnlyDependency";
        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        builder.DefineDynamicModule(name).DefineType("Marker", TypeAttributes.Public).CreateType();
        builder.Save(Path.Combine(_tempDir, name + ".dll"));

        var ctx = new BowirePluginLoadContext(_tempDir);
        Assert.True(ctx.IsShared(name));
        Assert.False(BowirePluginLoadContext.HostProvides(name));

        var loaded = (Assembly?)GetLoadMethod().Invoke(ctx, [new AssemblyName(name)]);

        Assert.NotNull(loaded);
        Assert.Equal(name, loaded!.GetName().Name);
        Assert.Same(ctx, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded));
        ctx.Unload();
    }

    [Fact]
    public void A_Shared_Assembly_The_Host_Has_Still_Comes_From_The_Host()
    {
        // The contract and the framework keep one identity across contexts.
        var ctx = new BowirePluginLoadContext(_tempDir);
        Assert.Null(GetLoadMethod().Invoke(ctx, [new AssemblyName("Microsoft.AspNetCore.Http.Abstractions")]));
        Assert.Null(GetLoadMethod().Invoke(ctx, [new AssemblyName("Kuestenlogik.Bowire")]));
    }

    [Fact]
    public void LoadUnmanagedDll_With_Missing_Native_Returns_Zero_Pointer()
    {
        // Unmanaged DLL resolver — both the bare name and the
        // ".dll"-appended fallback miss, so the override returns
        // IntPtr.Zero and the runtime raises DllNotFoundException at
        // the call site.
        var ctx = new BowirePluginLoadContext(_tempDir);
        var loadUnmanaged = typeof(BowirePluginLoadContext).GetMethod(
            "LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loadUnmanaged);

        var result = (IntPtr)loadUnmanaged!.Invoke(ctx, ["totally-missing-native-ghost"])!;

        Assert.Equal(IntPtr.Zero, result);
    }
}

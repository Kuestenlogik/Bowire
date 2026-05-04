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
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
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

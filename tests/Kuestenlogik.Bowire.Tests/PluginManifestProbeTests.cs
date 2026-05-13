// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for the pre-load probe that reads a plugin manifest's
/// <c>Kuestenlogik.Bowire</c> reference version from PE metadata without
/// loading the assembly into the runtime. The probe is the gate the
/// plugin loader uses to surface contract-version mismatches as
/// <see cref="PluginLoadStatus.ContractMajorMismatch"/> instead of
/// crashing inside the protocol-registry's reflection scan.
/// </summary>
public sealed class PluginManifestProbeTests
{
    [Fact]
    public void ReadReferencedBowireVersion_NoSuchFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll");
        Assert.Null(PluginManifestProbe.ReadReferencedBowireVersion(missing));
    }

    [Fact]
    public void ReadReferencedBowireVersion_NotAnAssembly_ReturnsNull()
    {
        // Random bytes — PEReader's HasMetadata returns false, or the
        // ctor throws BadImageFormatException; either way the probe
        // catches and returns null so the loader can decide to attempt
        // the load anyway and let the runtime emit its own error.
        var path = Path.Combine(Path.GetTempPath(), "junk-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03]); // "MZ" then garbage
            Assert.Null(PluginManifestProbe.ReadReferencedBowireVersion(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReadReferencedBowireVersion_AssemblyReferencingBowire_ReturnsTheReferenceVersion()
    {
        // The test assembly itself references Kuestenlogik.Bowire
        // (project reference in the .csproj), so its on-disk DLL is
        // a guaranteed probe target. Same major (1.x) as the host's
        // Kuestenlogik.Bowire, so we only assert the major matches
        // rather than pinning a full version (saves the test from
        // breaking on every patch bump). The earlier version of this
        // test pointed at Kuestenlogik.Bowire.Mock.dll via
        // typeof(IBowireMockEmitter).Assembly.Location, but the
        // runtime sometimes returns an unrelated load location for
        // assemblies loaded in unusual contexts — using the test
        // assembly's own location dodges that ambiguity.
        var thisAssemblyPath = typeof(PluginManifestProbeTests).Assembly.Location;
        Assert.True(File.Exists(thisAssemblyPath));

        var refVersion = PluginManifestProbe.ReadReferencedBowireVersion(thisAssemblyPath);
        Assert.NotNull(refVersion);
        Assert.Equal(PluginManifestProbe.HostBowireVersion.Major, refVersion!.Major);
    }

    [Fact]
    public void ReadReferencedBowireVersion_AssemblyWithoutBowireReference_ReturnsNull()
    {
        // System.Linq.dll exists and is a real assembly but never
        // references Kuestenlogik.Bowire — confirms the probe returns
        // null only when the reference is truly absent, not when the
        // PE read itself fails. (The probe later treats null as
        // "couldn't determine version" and waves the load through.)
        var linqPath = typeof(System.Linq.Enumerable).Assembly.Location;
        Assert.True(File.Exists(linqPath));

        Assert.Null(PluginManifestProbe.ReadReferencedBowireVersion(linqPath));
    }

    [Fact]
    public void IsContractCompatible_NullPluginVersion_TreatedAsCompatible()
    {
        // Null on the plugin side means "couldn't probe" — the loader
        // waves the load through and lets the runtime surface its own
        // error. Conservative call: we'd rather attempt a load than
        // reject a plugin whose metadata read failed for an unrelated
        // reason (locked file, ill-formed PE on a foreign arch, …).
        Assert.True(PluginManifestProbe.IsContractCompatible(null, new Version(1, 3, 0, 0)));
    }

    [Fact]
    public void IsContractCompatible_SameMajor_Compatible()
    {
        Assert.True(PluginManifestProbe.IsContractCompatible(new Version(1, 3, 0, 0), new Version(1, 3, 0, 0)));
        Assert.True(PluginManifestProbe.IsContractCompatible(new Version(1, 2, 5, 0), new Version(1, 3, 0, 0)));
        Assert.True(PluginManifestProbe.IsContractCompatible(new Version(1, 9, 9, 9), new Version(1, 3, 0, 0)));
    }

    [Fact]
    public void IsContractCompatible_DifferentMajor_Incompatible()
    {
        Assert.False(PluginManifestProbe.IsContractCompatible(new Version(2, 0, 0, 0), new Version(1, 3, 0, 0)));
        Assert.False(PluginManifestProbe.IsContractCompatible(new Version(0, 9, 0, 0), new Version(1, 3, 0, 0)));
        Assert.False(PluginManifestProbe.IsContractCompatible(new Version(1, 0, 0, 0), new Version(2, 0, 0, 0)));
    }

    [Fact]
    public void HostBowireVersion_Override_TakesPrecedence()
    {
        var previous = PluginManifestProbe.HostVersionOverride;
        try
        {
            PluginManifestProbe.HostVersionOverride = new Version(42, 7, 0, 0);
            Assert.Equal(new Version(42, 7, 0, 0), PluginManifestProbe.HostBowireVersion);
        }
        finally
        {
            PluginManifestProbe.HostVersionOverride = previous;
        }
    }
}

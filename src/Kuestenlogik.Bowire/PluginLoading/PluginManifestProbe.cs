// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Inspects a plugin's manifest assembly on disk via the BCL's portable
/// metadata reader — reads metadata only, never loads the assembly into
/// the runtime. Used by the loader to catch contract version mismatches
/// before the plugin ALC is even created, so the failure surfaces as a
/// structured <see cref="PluginLoadResult"/> instead of a silent skip
/// in the protocol-registry's <c>IsAssignableFrom</c> check.
/// </summary>
/// <remarks>
/// <para>
/// Implementation uses <see cref="PEReader"/> + <see cref="MetadataReader"/>
/// directly because both ship in the BCL — no extra NuGet dependency.
/// <c>MetadataLoadContext</c> would be slightly more ergonomic but lives
/// in the optional <c>System.Reflection.MetadataLoadContext</c> package
/// and pulls a transitive PathAssemblyResolver setup that needs the
/// runtime's trusted-platform-assemblies list — overkill when all we
/// need is the AssemblyRef table.
/// </para>
/// </remarks>
internal static class PluginManifestProbe
{
    private const string BowireContractAssemblyName = "Kuestenlogik.Bowire";

    /// <summary>
    /// Read the plugin manifest's referenced <c>Kuestenlogik.Bowire</c>
    /// version from metadata without loading the assembly. Returns
    /// <c>null</c> when the manifest doesn't reference Bowire at all
    /// (means it isn't a plugin), when the file isn't a valid assembly,
    /// or when the metadata read throws (corrupt DLL etc.).
    /// </summary>
    public static Version? ReadReferencedBowireVersion(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                var asmRef = reader.GetAssemblyReference(handle);
                var name = reader.GetString(asmRef.Name);
                if (string.Equals(name, BowireContractAssemblyName, StringComparison.Ordinal))
                    return asmRef.Version;
            }
        }
        catch
        {
            // Corrupt PE / not an assembly — caller treats null as
            // "couldn't probe, allow the load and let runtime surface
            // its own error".
        }
        return null;
    }

    /// <summary>
    /// Check whether <paramref name="pluginRefVersion"/> is compatible
    /// with <paramref name="hostVersion"/>. Same major = compatible;
    /// different major = breaking. Null on either side is conservatively
    /// treated as compatible — we'd rather attempt the load and surface
    /// any runtime error than reject a plugin whose Bowire reference
    /// version we couldn't read.
    /// </summary>
    public static bool IsContractCompatible(Version? pluginRefVersion, Version hostVersion)
    {
        if (pluginRefVersion is null) return true;
        return pluginRefVersion.Major == hostVersion.Major;
    }

    /// <summary>
    /// The host's compiled-in <c>Kuestenlogik.Bowire</c> assembly
    /// version. Read once at static-init time; tests can override via
    /// <see cref="HostVersionOverride"/> for deterministic version-gate
    /// coverage without rewriting the assembly.
    /// </summary>
    public static Version HostBowireVersion =>
        HostVersionOverride ?? typeof(PluginManifestProbe).Assembly.GetName().Version
            ?? new Version(0, 0, 0, 0);

    /// <summary>Test seam — set non-null to force <see cref="HostBowireVersion"/> to a specific value.</summary>
    public static Version? HostVersionOverride { get; set; }
}

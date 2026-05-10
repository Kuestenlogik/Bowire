// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tiny factory for building <c>.nupkg</c> archives in memory without
/// touching the network. A nupkg is just a ZIP with a <c>.nuspec</c> at
/// the root + (optionally) <c>lib/&lt;tfm&gt;/*.dll</c> entries — that
/// minimum subset is enough to drive <see cref="NuGet.Packaging.PackageArchiveReader"/>
/// through every code path the offline installer touches without
/// resolving anything against nuget.org.
/// </summary>
internal static class NuGetPackageInstallerTests_NupkgFactory
{
    public static byte[] NoDeps(string id, string version, string targetFramework = "net10.0") =>
        Build(id, version, targetFramework, dependencyId: null, dependencyVersion: null, includeLibDll: true);

    public static byte[] WithDep(string id, string version, string depId, string? depVersion,
        string targetFramework = "net10.0") =>
        Build(id, version, targetFramework, depId, depVersion, includeLibDll: true);

    public static byte[] Build(
        string packageId,
        string version,
        string targetFramework,
        string? dependencyId,
        string? dependencyVersion,
        bool includeLibDll)
    {
        var nuspecXml = BuildNuspec(packageId, version, targetFramework, dependencyId, dependencyVersion);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec", CompressionLevel.Fastest);
            using (var s = nuspecEntry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            {
                w.Write(nuspecXml);
            }

            if (includeLibDll)
            {
                // PE-shaped placeholder; ExtractLibsAsync is a byte copy
                // and the host-skip filter keys on basename only, so the
                // contents never get inspected.
                var libEntry = archive.CreateEntry($"lib/{targetFramework}/Stub.dll", CompressionLevel.Fastest);
                using var s = libEntry.Open();
                s.WriteByte(0x4D); // 'M'
                s.WriteByte(0x5A); // 'Z'
                for (var i = 0; i < 30; i++) s.WriteByte(0);
            }
        }
        return ms.ToArray();
    }

    private static string BuildNuspec(
        string id, string version, string targetFramework,
        string? dependencyId, string? dependencyVersion)
    {
        var deps = "";
        if (!string.IsNullOrEmpty(dependencyId))
        {
            var verAttr = dependencyVersion is null
                ? ""
                : $" version=\"{dependencyVersion}\"";
            deps = $"""
                <dependencies>
                  <group targetFramework="{targetFramework}">
                    <dependency id="{dependencyId}"{verAttr} />
                  </group>
                </dependencies>
                """;
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>Test package {id}</description>
                {deps}
              </metadata>
            </package>
            """;
    }
}

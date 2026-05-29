// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Sidecar manifest parsing + discovery — pure-helper coverage that
/// doesn't need to spawn a real process.
/// </summary>
public class SidecarPluginManifestTests
{
    [Fact]
    public void TryLoadFromFile_Parses_Full_Manifest()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "packageId": "Acme.Bowire.Protocol.Zenoh",
              "protocol": {
                "id": "zenoh",
                "name": "Zenoh",
                "iconSvg": "<svg/>"
              },
              "executable": "bin/zenoh-sidecar",
              "args": ["--quiet"],
              "envPrefix": "BOWIRE_ZENOH_",
              "shutdownTimeoutMs": 5000
            }
            """);

            var m = SidecarPluginManifest.TryLoadFromFile(tmp);
            Assert.NotNull(m);
            Assert.Equal("Acme.Bowire.Protocol.Zenoh", m!.PackageId);
            Assert.Equal("zenoh", m.Protocol.Id);
            Assert.Equal("Zenoh", m.Protocol.Name);
            Assert.Equal("<svg/>", m.Protocol.IconSvg);
            Assert.Equal("bin/zenoh-sidecar", m.Executable);
            Assert.Equal(["--quiet"], m.Args);
            Assert.Equal("BOWIRE_ZENOH_", m.EnvPrefix);
            Assert.Equal(5000, m.ShutdownTimeoutMs);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void TryLoadFromFile_Defaults_Apply_When_Optional_Fields_Missing()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            { "packageId": "X", "protocol": {"id":"x","name":"X"}, "executable": "x" }
            """);
            var m = SidecarPluginManifest.TryLoadFromFile(tmp);
            Assert.NotNull(m);
            Assert.Null(m!.Args);
            Assert.Equal("BOWIRE_", m.EnvPrefix);
            Assert.Equal(3000, m.ShutdownTimeoutMs);
            Assert.Null(m.Protocol.IconSvg);
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TryLoadFromFile_Returns_Null_For_Blank_Path(string path)
    {
        Assert.Null(SidecarPluginManifest.TryLoadFromFile(path));
    }

    [Fact]
    public void TryLoadFromFile_Returns_Null_For_Missing_File()
    {
        Assert.Null(SidecarPluginManifest.TryLoadFromFile(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void TryLoadFromFile_Returns_Null_For_Malformed_Json()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "{ not json");
            Assert.Null(SidecarPluginManifest.TryLoadFromFile(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Discover_Returns_Empty_For_Missing_Root()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Empty(SidecarPluginDiscovery.Discover(nonexistent));
    }

    [Fact]
    public void Discover_Skips_Subdirs_Without_Plugin_Json()
    {
        var root = Path.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "no-manifest"));
        try
        {
            Assert.Empty(SidecarPluginDiscovery.Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Skips_Manifest_Missing_Executable()
    {
        var root = Path.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "broken");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(sub, "sidecar.json"),
                """{ "packageId":"X", "protocol":{"id":"x","name":"X"}, "executable":"" }""");
            Assert.Empty(SidecarPluginDiscovery.Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Honours_Disabled_Set()
    {
        var root = Path.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "zenoh");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(sub, "sidecar.json"),
                """{ "packageId":"X", "protocol":{"id":"zenoh","name":"Zenoh"}, "executable":"x" }""");

            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zenoh" };
            Assert.Empty(SidecarPluginDiscovery.Discover(root, disabled));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Surfaces_One_Sidecar_Per_Valid_Manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var subA = Path.Combine(root, "plugin-a");
        var subB = Path.Combine(root, "plugin-b");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        try
        {
            File.WriteAllText(Path.Combine(subA, "sidecar.json"),
                """{ "packageId":"A", "protocol":{"id":"a","name":"A"}, "executable":"a.exe" }""");
            File.WriteAllText(Path.Combine(subB, "sidecar.json"),
                """{ "packageId":"B", "protocol":{"id":"b","name":"B"}, "executable":"b.exe" }""");

            var sidecars = SidecarPluginDiscovery.Discover(root);
            Assert.Equal(2, sidecars.Count);
            Assert.Contains(sidecars, s => s.Id == "a");
            Assert.Contains(sidecars, s => s.Id == "b");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SidecarBowireProtocol_Uses_Manifest_Metadata_Before_Init()
    {
        var manifest = new SidecarPluginManifest(
            PackageId: "Acme.Bowire.Protocol.Demo",
            Protocol: new SidecarProtocolMetadata("demo", "Demo", "<svg/>"),
            Executable: "demo");
        var plugin = new SidecarBowireProtocol(manifest, Path.GetTempPath());
        Assert.Equal("demo", plugin.Id);
        Assert.Equal("Demo", plugin.Name);
        Assert.Equal("<svg/>", plugin.IconSvg);
    }

    [Fact]
    public void SidecarBowireProtocol_Falls_Back_To_Generic_Icon()
    {
        var manifest = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("x", "X", IconSvg: null),
            Executable: "x");
        var plugin = new SidecarBowireProtocol(manifest, Path.GetTempPath());
        Assert.Contains("<svg", plugin.IconSvg);
    }
}

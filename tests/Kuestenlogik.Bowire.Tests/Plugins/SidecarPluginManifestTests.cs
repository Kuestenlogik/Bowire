// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            SafePath.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
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
        var nonexistent = SafePath.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Empty(SidecarPluginDiscovery.Discover(nonexistent));
    }

    [Fact]
    public void Discover_Skips_Subdirs_Without_Plugin_Json()
    {
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SafePath.Combine(root, "no-manifest"));
        try
        {
            Assert.Empty(SidecarPluginDiscovery.Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Skips_Manifest_Missing_Executable()
    {
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = SafePath.Combine(root, "broken");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(SafePath.Combine(sub, "sidecar.json"),
                """{ "packageId":"X", "protocol":{"id":"x","name":"X"}, "executable":"" }""");
            Assert.Empty(SidecarPluginDiscovery.Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Surfaces_Http_Transport_Manifest_Without_Executable()
    {
        // #415: an http sidecar legitimately has no executable — only a url.
        // Discovery must load it (regression: the old guard hardcoded the
        // executable check and dropped every http manifest).
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = SafePath.Combine(root, "httpsc");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(SafePath.Combine(sub, "sidecar.json"),
                """{ "packageId":"H", "protocol":{"id":"h","name":"H"}, "transport":"http", "url":"http://localhost:7000/bowire" }""");

            var sidecars = SidecarPluginDiscovery.Discover(root);
            var s = Assert.Single(sidecars);
            Assert.Equal("h", s.Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Skips_Http_Manifest_Missing_Url()
    {
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = SafePath.Combine(root, "brokenhttp");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(SafePath.Combine(sub, "sidecar.json"),
                """{ "packageId":"H", "protocol":{"id":"h","name":"H"}, "transport":"http" }""");
            Assert.Empty(SidecarPluginDiscovery.Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Honours_Disabled_Set()
    {
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var sub = SafePath.Combine(root, "zenoh");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(SafePath.Combine(sub, "sidecar.json"),
                """{ "packageId":"X", "protocol":{"id":"zenoh","name":"Zenoh"}, "executable":"x" }""");

            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zenoh" };
            Assert.Empty(SidecarPluginDiscovery.Discover(root, disabled));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_Surfaces_One_Sidecar_Per_Valid_Manifest()
    {
        var root = SafePath.Combine(Path.GetTempPath(), "bowire-sidecar-test-" + Guid.NewGuid().ToString("N"));
        var subA = SafePath.Combine(root, "plugin-a");
        var subB = SafePath.Combine(root, "plugin-b");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        try
        {
            File.WriteAllText(SafePath.Combine(subA, "sidecar.json"),
                """{ "packageId":"A", "protocol":{"id":"a","name":"A"}, "executable":"a.exe" }""");
            File.WriteAllText(SafePath.Combine(subB, "sidecar.json"),
                """{ "packageId":"B", "protocol":{"id":"b","name":"B"}, "executable":"b.exe" }""");

            var sidecars = SidecarPluginDiscovery.Discover(root);
            Assert.Equal(2, sidecars.Count);
            Assert.Contains(sidecars, s => s.Id == "a");
            Assert.Contains(sidecars, s => s.Id == "b");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------------- #417: published JSON Schema stays in sync ----------------

    private static JsonElement LoadManifestSchema()
    {
        // Walk up from the test binary to the repo root (the dir that owns
        // Directory.Build.props) and read the published schema from site/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var schemaPath = Path.Combine(dir!.FullName, "site", "schemas", "sidecar.schema.json");
        Assert.True(File.Exists(schemaPath), "Missing published schema at " + schemaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ManifestSchema_Has_Expected_Id_And_Required_Fields()
    {
        var schema = LoadManifestSchema();
        Assert.Equal("https://bowire.io/schemas/sidecar.schema.json", schema.GetProperty("$id").GetString());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("packageId", required);
        Assert.Contains("protocol", required);
    }

    [Fact]
    public void ManifestSchema_Properties_Cover_Every_Manifest_Field()
    {
        // Drift guard: every JsonPropertyName on the manifest record (and its
        // nested protocol metadata) must have a matching schema property, so the
        // published schema can't silently fall behind the model.
        var schema = LoadManifestSchema();
        var props = schema.GetProperty("properties");

        foreach (var name in JsonPropertyNames(typeof(SidecarPluginManifest)))
            Assert.True(props.TryGetProperty(name, out _), $"Schema is missing manifest field '{name}'.");

        var protocolProps = props.GetProperty("protocol").GetProperty("properties");
        foreach (var name in JsonPropertyNames(typeof(SidecarProtocolMetadata)))
            Assert.True(protocolProps.TryGetProperty(name, out _), $"Schema is missing protocol field '{name}'.");
    }

    private static IEnumerable<string> JsonPropertyNames(Type recordType) =>
        recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(n => !string.IsNullOrEmpty(n))!
            .Cast<string>();

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

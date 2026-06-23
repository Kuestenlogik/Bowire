// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Branch-coverage uplift for <see cref="SidecarPluginManifest"/>.
/// Complements the existing <c>SidecarPluginManifestTests</c> by
/// hitting <see cref="SidecarPluginManifest.TryParse"/>'s
/// <c>JsonException</c> branch (the file-based path already exercises
/// the other half), the <see cref="SidecarPluginManifest.TryLoadFromFile"/>
/// IOException branch (file locked by another handle), and the
/// http-transport-specific <see cref="SidecarPluginManifest.IsValid"/>
/// + <see cref="SidecarPluginManifest.IsHttp"/> branches.
/// </summary>
public sealed class SidecarPluginManifestCoverageTests
{
    [Fact]
    public void TryParse_Returns_Null_For_Whitespace()
    {
        Assert.Null(SidecarPluginManifest.TryParse(""));
        Assert.Null(SidecarPluginManifest.TryParse("   "));
        Assert.Null(SidecarPluginManifest.TryParse("\t\n"));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Malformed_Json()
    {
        Assert.Null(SidecarPluginManifest.TryParse("{not json"));
        Assert.Null(SidecarPluginManifest.TryParse("[]"));   // wrong root
    }

    [Fact]
    public void TryParse_Parses_Minimal_Stdio_Manifest()
    {
        var m = SidecarPluginManifest.TryParse(
            """{"packageId":"X","protocol":{"id":"x","name":"X"},"executable":"x"}""");

        Assert.NotNull(m);
        Assert.Equal("X", m!.PackageId);
        Assert.False(m.IsHttp);
        Assert.True(m.IsValid);
    }

    [Fact]
    public void TryParse_Parses_Http_Transport_Manifest()
    {
        var m = SidecarPluginManifest.TryParse(
            """
            {
              "packageId":"X",
              "protocol":{"id":"x","name":"X"},
              "transport":"http",
              "url":"http://localhost:7000/bowire"
            }
            """);

        Assert.NotNull(m);
        Assert.True(m!.IsHttp);
        Assert.True(m.IsValid);
        Assert.Equal("http://localhost:7000/bowire", m.Url);
    }

    [Fact]
    public void IsValid_False_For_Http_Manifest_Without_Url()
    {
        // http transport demands a Url. Without one the manifest is
        // valid JSON but functionally unusable — IsValid catches it.
        var m = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("x", "X"),
            Transport: "http",
            Url: null);

        Assert.True(m.IsHttp);
        Assert.False(m.IsValid);
    }

    [Fact]
    public void IsValid_False_For_Stdio_Manifest_Without_Executable()
    {
        var m = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("x", "X"),
            Executable: "");

        Assert.False(m.IsHttp);
        Assert.False(m.IsValid);
    }

    [Fact]
    public void IsValid_False_When_Protocol_Id_Missing()
    {
        // Empty protocol id is unusable — IsValid catches it even when
        // the transport side has a target.
        var m = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("", "X"),
            Executable: "demo");

        Assert.False(m.IsValid);
    }

    [Fact]
    public void IsHttp_Case_Insensitive()
    {
        var lower = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("x", "X"),
            Transport: "http",
            Url: "http://l");
        var upper = lower with { Transport = "HTTP" };
        var mixed = lower with { Transport = "Http" };

        Assert.True(lower.IsHttp);
        Assert.True(upper.IsHttp);
        Assert.True(mixed.IsHttp);
    }

    [Fact]
    public void TryParse_Honours_Trailing_Commas_And_Comments()
    {
        // The JsonSerializerOptions allow trailing commas + skip
        // comments — pin that the manifest parses both extensions
        // (the schema is hand-edited by sidecar authors, who often
        // leave trailing commas in).
        var json = """
            // Top-level comment
            {
              "packageId": "X",
              "protocol": { "id": "x", "name": "X" },
              "executable": "x", // inline comment, trailing comma below
            }
            """;
        var m = SidecarPluginManifest.TryParse(json);
        Assert.NotNull(m);
    }

    [Fact]
    public void TryLoadFromFile_Returns_Null_When_Path_Is_Locked()
    {
        // Open the file with a deny-share lock — File.OpenRead inside
        // TryLoadFromFile then throws IOException, which the catch
        // arm swallows + returns null.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """{"packageId":"X","protocol":{"id":"x","name":"X"},"executable":"x"}""");
            using var locker = new FileStream(
                tmp, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Null(SidecarPluginManifest.TryLoadFromFile(tmp));
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Constructor_Defaults_Apply_When_Optional_Args_Omitted()
    {
        var m = new SidecarPluginManifest(
            PackageId: "X",
            Protocol: new SidecarProtocolMetadata("x", "X"));

        // Default values from the record's parameter declarations.
        Assert.Equal("", m.Executable);
        Assert.Null(m.Args);
        Assert.Equal("BOWIRE_", m.EnvPrefix);
        Assert.Equal(3000, m.ShutdownTimeoutMs);
        Assert.Null(m.Version);
        Assert.Equal("stdio", m.Transport);
        Assert.Null(m.Url);
    }

    [Fact]
    public void FileName_Is_Sidecar_Json()
    {
        Assert.Equal("sidecar.json", SidecarPluginManifest.FileName);
    }
}

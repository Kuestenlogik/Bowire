// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// Coverage for <see cref="BowireMcpResources"/>. Each test points
/// the <c>HomeDirOverride</c> seam at a per-test temp directory so
/// the resource methods read fixture files instead of the user's
/// real <c>~/.bowire/</c>.
/// </summary>
public sealed class BowireMcpResourcesTests : IDisposable
{
    private readonly string _homeDir;
    private readonly string _bowireDir;
    private readonly string? _originalOverride;

    public BowireMcpResourcesTests()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), "bowire-resources-" + Guid.NewGuid().ToString("N")[..8]);
        _bowireDir = Path.Combine(_homeDir, ".bowire");
        Directory.CreateDirectory(_bowireDir);

        _originalOverride = BowireMcpTools.HomeDirOverride;
        BowireMcpResources.HomeDirOverride = _homeDir;
    }

    public void Dispose()
    {
        BowireMcpResources.HomeDirOverride = _originalOverride;
        try { Directory.Delete(_homeDir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string filename, string content)
        => File.WriteAllText(Path.Combine(_bowireDir, filename), content);

    [Fact]
    public void Environments_MissingFile_ReturnsEmptyList()
    {
        var resource = BowireMcpResources.Environments();
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("bowire://environments", resource.Uri);
        Assert.Equal("application/json", resource.MimeType);
        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public void Environments_ReturnsFileContent_Verbatim()
    {
        WriteFile("environments.json", """[{"id":"dev","name":"Dev","servers":["https://api.dev"]}]""");

        var resource = BowireMcpResources.Environments();
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("dev", doc.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public void RecordingsIndex_MissingFile_ReturnsEmpty()
    {
        var resource = BowireMcpResources.RecordingsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.True(doc.RootElement.TryGetProperty("recordings", out var arr));
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public void RecordingsIndex_BuildsSummary_FromArrayLayout()
    {
        WriteFile("recordings.json", """
            [
              {"id":"r1","name":"login flow","protocol":"rest","createdAt":"2026-05-25T00:00:00Z","steps":[{},{},{}]},
              {"id":"r2","name":"checkout","protocol":"grpc","createdAt":"2026-05-25T01:00:00Z","steps":[{}]}
            ]
            """);

        var resource = BowireMcpResources.RecordingsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);

        var recordings = doc.RootElement.GetProperty("recordings");
        Assert.Equal(2, recordings.GetArrayLength());
        Assert.Equal(3, recordings[0].GetProperty("stepCount").GetInt32());
        Assert.Equal(1, recordings[1].GetProperty("stepCount").GetInt32());
        Assert.Equal("rest", recordings[0].GetProperty("protocol").GetString());
    }

    [Fact]
    public void Recording_ReturnsMatchingItem_FromTopLevelArray()
    {
        WriteFile("recordings.json", """
            [{"id":"r1","name":"x","steps":[]},{"id":"r2","name":"y","steps":[]}]
            """);

        var resource = BowireMcpResources.Recording("r2");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("r2", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("bowire://recordings/r2", resource.Uri);
    }

    [Fact]
    public void Recording_UnknownId_ReturnsErrorObject()
    {
        WriteFile("recordings.json", """[{"id":"r1"}]""");

        var resource = BowireMcpResources.Recording("does-not-exist");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("does-not-exist", err.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_FromObjectLayout_AlsoMatches()
    {
        WriteFile("recordings.json", """
            { "recordings": [{"id":"r-obj","steps":[]}] }
            """);

        var resource = BowireMcpResources.Recording("r-obj");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("r-obj", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void CollectionsIndex_MissingFile_DoesNotThrow()
    {
        var resource = BowireMcpResources.CollectionsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);
        Assert.True(doc.RootElement.TryGetProperty("items", out _));
    }

    [Fact]
    public void Collection_ReturnsMatchingEntry()
    {
        WriteFile("collections.json", """
            { "collections": [{"id":"c1","name":"Acme"}, {"id":"c2","name":"Beta"}] }
            """);

        var resource = BowireMcpResources.Collection("c1");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("Acme", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void FlowsIndex_MissingFile_DoesNotThrow()
    {
        var resource = BowireMcpResources.FlowsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);
        Assert.True(doc.RootElement.TryGetProperty("items", out _));
    }

    [Fact]
    public void Flow_ReturnsMatchingEntry()
    {
        WriteFile("flows.json", """
            [{"id":"f1","steps":[]},{"id":"f2","steps":[{}]}]
            """);

        var resource = BowireMcpResources.Flow("f2");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("f2", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Plugins_NoPluginDir_ReturnsEmpty()
    {
        var resource = BowireMcpResources.Plugins();
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.True(doc.RootElement.TryGetProperty("plugins", out var arr));
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public void Plugins_ListsValidEntries_SkipsBrokenManifests()
    {
        var pluginRoot = Path.Combine(_bowireDir, "plugins");
        var goodDir = Path.Combine(pluginRoot, "Acme.Plugin.Good");
        var badDir = Path.Combine(pluginRoot, "Acme.Plugin.Broken");
        var noManifestDir = Path.Combine(pluginRoot, "Acme.Plugin.NoManifest");
        Directory.CreateDirectory(goodDir);
        Directory.CreateDirectory(badDir);
        Directory.CreateDirectory(noManifestDir);
        File.WriteAllText(Path.Combine(goodDir, "plugin.json"),
            """{"packageId":"Acme.Plugin.Good","version":"1.2.3"}""");
        File.WriteAllText(Path.Combine(badDir, "plugin.json"),
            "{ not valid json");

        var resource = BowireMcpResources.Plugins();
        using var doc = JsonDocument.Parse(resource.Text!);

        var plugins = doc.RootElement.GetProperty("plugins");
        // Good lands; Broken JSON is skipped silently; missing manifest skipped.
        Assert.Equal(1, plugins.GetArrayLength());
        Assert.Equal("Acme.Plugin.Good", plugins[0].GetProperty("packageId").GetString());
        Assert.Equal("1.2.3", plugins[0].GetProperty("version").GetString());
    }
}

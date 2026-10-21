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
[Collection(nameof(BowireConfigFixture))]
public sealed class BowireMcpResourcesTests : IDisposable
{
    private readonly string _homeDir;
    private readonly string _bowireDir;
    private readonly string? _originalOverride;

    public BowireMcpResourcesTests()
    {
        _homeDir = SafePath.Combine(Path.GetTempPath(), "bowire-resources-" + Guid.NewGuid().ToString("N")[..8]);
        _bowireDir = SafePath.Combine(_homeDir, ".bowire");
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
        => File.WriteAllText(SafePath.Combine(_bowireDir, filename), content);

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
    public void RecordingsIndex_A_Bare_Array_Is_Not_The_Store_Layout()
    {
        // No store writes a bare array (#887); the index reads the
        // {"recordings":[…]} layout only, the same as bowire.record.list.
        WriteFile("recordings.json", """[{"id":"r1","steps":[{}]}]""");

        var resource = BowireMcpResources.RecordingsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal(0, doc.RootElement.GetProperty("recordings").GetArrayLength());
    }

    [Fact]
    public void Recording_ReturnsMatchingItem()
    {
        WriteFile("recordings.json", """
            { "recordings": [{"id":"r1","name":"x","steps":[]},{"id":"r2","name":"y","steps":[]}] }
            """);

        var resource = BowireMcpResources.Recording("r2");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("r2", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("bowire://recordings/r2", resource.Uri);
    }

    [Fact]
    public void Recording_UnknownId_ReturnsErrorObject()
    {
        WriteFile("recordings.json", """{ "recordings": [{"id":"r1"}] }""");

        var resource = BowireMcpResources.Recording("does-not-exist");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("does-not-exist", err.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_A_Bare_Array_Is_Not_The_Store_Layout()
    {
        WriteFile("recordings.json", """[{"id":"r1","steps":[]}]""");

        var resource = BowireMcpResources.Recording("r1");

        Assert.Equal("{}", resource.Text);
    }

    [Fact]
    public void RecordingsIndex_BuildsSummary_FromStoreLayout()
    {
        // The layout the recording store and bowire.record.stop write,
        // createdAt as Unix milliseconds included.
        WriteFile("recordings.json", """
            { "recordings": [
              {"id":"r1","name":"login flow","createdAt":1767225600000,"steps":[{},{}]},
              {"id":"r2","name":"checkout","steps":[]}
            ] }
            """);

        var resource = BowireMcpResources.RecordingsIndex();
        using var doc = JsonDocument.Parse(resource.Text!);

        var recordings = doc.RootElement.GetProperty("recordings");
        Assert.Equal(2, recordings.GetArrayLength());
        Assert.Equal("r1", recordings[0].GetProperty("id").GetString());
        Assert.Equal("1767225600000", recordings[0].GetProperty("createdAt").GetString());
        Assert.Equal(2, recordings[0].GetProperty("stepCount").GetInt32());
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    public void Recording_UnknownId_ErrorBody_IsValidJson_ForAnyId(string id)
    {
        WriteFile("recordings.json", """{ "recordings": [{"id":"r1"}] }""");

        var resource = BowireMcpResources.Recording(id);
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Contains(id, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_An_Entry_With_A_NonString_Id_Does_Not_Hide_The_Others()
    {
        WriteFile("recordings.json", """
            { "recordings": [{"id":42,"steps":[]},{"id":null},{"id":"r2","steps":[]}] }
            """);

        var resource = BowireMcpResources.Recording("r2");
        using var doc = JsonDocument.Parse(resource.Text!);

        Assert.Equal("r2", doc.RootElement.GetProperty("id").GetString());
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
            { "flows": [{"id":"f1","steps":[]},{"id":"f2","steps":[{}]}] }
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
        var pluginRoot = SafePath.Combine(_bowireDir, "plugins");
        var goodDir = SafePath.Combine(pluginRoot, "Acme.Plugin.Good");
        var badDir = SafePath.Combine(pluginRoot, "Acme.Plugin.Broken");
        var noManifestDir = SafePath.Combine(pluginRoot, "Acme.Plugin.NoManifest");
        Directory.CreateDirectory(goodDir);
        Directory.CreateDirectory(badDir);
        Directory.CreateDirectory(noManifestDir);
        File.WriteAllText(SafePath.Combine(goodDir, "plugin.json"),
            """{"packageId":"Acme.Plugin.Good","version":"1.2.3"}""");
        File.WriteAllText(SafePath.Combine(badDir, "plugin.json"),
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

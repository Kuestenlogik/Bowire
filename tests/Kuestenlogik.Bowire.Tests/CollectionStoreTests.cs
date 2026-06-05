// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="CollectionStore"/> — Postman-style named groups
/// of saved requests. Mirrors the <see cref="RecordingStoreTests"/>
/// shape: the internal <see cref="CollectionStore.StorePath"/> setter
/// redirects the file to a temp path so the disk round-trip is exercised
/// without touching the developer's real <c>~/.bowire/collections.json</c>.
/// </summary>
public sealed class CollectionStoreTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public CollectionStoreTests()
    {
        _originalPath = CollectionStore.StorePath;
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-collections-test-{Guid.NewGuid():N}",
            "collections.json");
        CollectionStore.StorePath = _tempPath;
    }

    public void Dispose()
    {
        CollectionStore.StorePath = _originalPath;
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_Missing_File_Returns_Empty_Envelope()
    {
        var json = CollectionStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("collections", out var collections));
        Assert.Equal(JsonValueKind.Array, collections.ValueKind);
        Assert.Equal(0, collections.GetArrayLength());
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Document()
    {
        const string Payload = """{"collections":[{"id":"c1","name":"Smoke","items":[]}]}""";

        CollectionStore.Save(Payload);

        var loaded = CollectionStore.Load();
        using var doc = JsonDocument.Parse(loaded);
        var collections = doc.RootElement.GetProperty("collections");
        Assert.Equal(1, collections.GetArrayLength());
        Assert.Equal("c1", collections[0].GetProperty("id").GetString());
        Assert.Equal("Smoke", collections[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Save_Creates_Parent_Directory()
    {
        // Tempfile points at a brand-new nested dir we never touched.
        // Save() is expected to mkdir -p on the way; if it ever
        // regresses to "throws DirectoryNotFoundException", the workbench
        // loses its first-ever-save path.
        Assert.False(File.Exists(_tempPath));
        CollectionStore.Save("""{"collections":[]}""");
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Save_Rejects_Empty_Payload()
    {
        Assert.Throws<ArgumentException>(() => CollectionStore.Save(string.Empty));
    }

    [Fact]
    public void Save_Rejects_Invalid_Json()
    {
        // The store validates JSON before writing so a caller bug
        // (mismatched braces, truncated stream) can't poison the file.
        // ThrowsAny covers JsonReaderException (the concrete System.Text.Json
        // type) as well as JsonException.
        Assert.ThrowsAny<JsonException>(() => CollectionStore.Save("{not json"));
    }

    [Fact]
    public void Load_Corrupt_File_Returns_Empty_Envelope_Without_Throwing()
    {
        var dir = Path.GetDirectoryName(_tempPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_tempPath, "{this is not json");

        var json = CollectionStore.Load();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("collections").GetArrayLength());
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="RecordingStore"/>. Mirrors the
/// <see cref="EnvironmentStoreTests"/> pattern — internal
/// <see cref="RecordingStore.StorePath"/> redirects to a temp path so
/// the Save round-trip can be exercised without touching the
/// developer's real <c>~/.bowire/recordings.json</c>.
/// </summary>
public sealed class RecordingStoreTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public RecordingStoreTests()
    {
        _originalPath = RecordingStore.StorePath;
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-rec-test-{Guid.NewGuid():N}",
            "recordings.json");
        RecordingStore.StorePath = _tempPath;
    }

    public void Dispose()
    {
        RecordingStore.StorePath = _originalPath;
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_Missing_File_Returns_Empty_Recordings_Array()
    {
        var json = RecordingStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out var recordings));
        Assert.Equal(JsonValueKind.Array, recordings.ValueKind);
        Assert.Equal(0, recordings.GetArrayLength());
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Document()
    {
        const string Compact = """{"recordings":[{"id":"r1","name":"Demo","steps":[]}]}""";

        RecordingStore.Save(Compact);

        var loaded = RecordingStore.Load();
        using var doc = JsonDocument.Parse(loaded);
        var recordings = doc.RootElement.GetProperty("recordings");
        Assert.Equal(1, recordings.GetArrayLength());
        Assert.Equal("Demo", recordings[0].GetProperty("name").GetString());

        // On-disk form is pretty-printed for manual editing.
        var raw = File.ReadAllText(_tempPath);
        Assert.Contains("\n", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_Creates_Missing_Directory()
    {
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        RecordingStore.Save("""{"recordings":[]}""");

        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Save_Throws_On_Malformed_Json_Without_Touching_Disk()
    {
        // The endpoint surface translates this into a 400 — pin the throw
        // here so the validation order (parse-first, write-after) stays
        // intact across refactors. ThrowsAny because JsonDocument.Parse
        // raises a JsonReaderException subclass.
        Assert.ThrowsAny<JsonException>(() => RecordingStore.Save("{ broken"));
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void Load_Corrupt_File_Falls_Back_To_Empty_Shape()
    {
        var dir = Path.GetDirectoryName(_tempPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_tempPath, "not json at all");

        var json = RecordingStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out _));
    }
}

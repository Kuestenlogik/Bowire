// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="EnvironmentStore"/>. The store's
/// <see cref="EnvironmentStore.StorePath"/> property is internal and
/// settable so the Save path can be exercised against a temp file
/// without touching the developer's real <c>~/.bowire/</c>.
/// </summary>
public sealed class EnvironmentStoreTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public EnvironmentStoreTests()
    {
        _originalPath = EnvironmentStore.StorePath;
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-env-test-{Guid.NewGuid():N}",
            "environments.json");
        EnvironmentStore.StorePath = _tempPath;
    }

    public void Dispose()
    {
        EnvironmentStore.StorePath = _originalPath;
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_Missing_File_Returns_Empty_Default_Shape()
    {
        // Brand-new temp path, no file → fallback to the empty literal.
        var json = EnvironmentStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("globals", out var globals));
        Assert.Equal(JsonValueKind.Object, globals.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("environments", out var envs));
        Assert.Equal(JsonValueKind.Array, envs.ValueKind);
        Assert.Equal(0, envs.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("activeEnvId", out var active));
        Assert.Equal("", active.GetString());
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Document_Pretty_Printed()
    {
        const string Compact = """{"globals":{"baseUrl":"https://api.example.com"},"environments":[{"id":"dev","name":"Dev"}],"activeEnvId":"dev"}""";

        EnvironmentStore.Save(Compact);

        // The round trip parses back to the same logical shape …
        var loaded = EnvironmentStore.Load();
        using var doc = JsonDocument.Parse(loaded);
        Assert.Equal("https://api.example.com",
            doc.RootElement.GetProperty("globals").GetProperty("baseUrl").GetString());
        Assert.Equal("dev", doc.RootElement.GetProperty("activeEnvId").GetString());

        // … and the on-disk file is the human-readable indented form.
        var raw = File.ReadAllText(_tempPath);
        Assert.Contains("\n", raw, StringComparison.Ordinal);
        Assert.Contains("  ", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_Creates_Missing_Directory()
    {
        // The parent directory doesn't exist — Save must create it before
        // writing, otherwise the very first write on a fresh machine fails.
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        EnvironmentStore.Save("""{"globals":{},"environments":[],"activeEnvId":""}""");

        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Save_Throws_On_Malformed_Json_Without_Touching_Disk()
    {
        // Save validates first — refuses to overwrite the file with garbage.
        // ThrowsAny because JsonDocument.Parse raises a JsonReaderException
        // subclass; the contract is "any JsonException", not the exact type.
        Assert.ThrowsAny<JsonException>(() => EnvironmentStore.Save("{ broken"));
        Assert.False(File.Exists(_tempPath), "Save must not touch disk on validation failure");
    }

    [Fact]
    public void Load_Corrupt_File_Falls_Back_To_Empty_Shape()
    {
        // Write garbage directly to the file, then call Load — the catch
        // returns the empty-default literal so the UI keeps rendering
        // even when the file got truncated mid-write.
        var dir = Path.GetDirectoryName(_tempPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_tempPath, "not json");

        var json = EnvironmentStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("environments", out _));
    }
}

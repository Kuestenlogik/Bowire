// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Schema-migration tests for <see cref="EnvironmentStore"/>. The
/// store's Load() back-fills missing top-level properties in-memory so
/// every consumer sees the current envelope shape regardless of upgrade
/// history. <see cref="EnvironmentStoreTests"/> covers the happy path
/// + corrupt-file recovery; this file targets the per-missing-property
/// migration branches that determine which fields get back-filled.
/// </summary>
public sealed class EnvironmentStoreSchemaMigrationTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public EnvironmentStoreSchemaMigrationTests()
    {
        _originalPath = EnvironmentStore.StorePath;
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-env-migrate-test-{Guid.NewGuid():N}",
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
    public void Load_File_Missing_Only_Globals_Backfills_Globals_Object()
    {
        // Pre-globals files: only `environments` + `activeEnvId` were
        // written. Load() must surface an empty `globals` object on top
        // so the workbench's substitution layer doesn't bomb.
        WriteRaw("""{"environments":[{"id":"e1","name":"Dev"}],"activeEnvId":"e1"}""");

        using var doc = JsonDocument.Parse(EnvironmentStore.Load());
        Assert.True(doc.RootElement.TryGetProperty("globals", out var globals));
        Assert.Equal(JsonValueKind.Object, globals.ValueKind);
        Assert.Empty(globals.EnumerateObject());
        // Existing properties must be preserved verbatim.
        Assert.Equal("e1", doc.RootElement.GetProperty("activeEnvId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public void Load_File_Missing_Only_Environments_Backfills_Empty_Array()
    {
        WriteRaw("""{"globals":{"region":"eu"},"activeEnvId":"e1"}""");

        using var doc = JsonDocument.Parse(EnvironmentStore.Load());
        Assert.True(doc.RootElement.TryGetProperty("environments", out var envs));
        Assert.Equal(JsonValueKind.Array, envs.ValueKind);
        Assert.Equal(0, envs.GetArrayLength());
        Assert.Equal("eu", doc.RootElement.GetProperty("globals").GetProperty("region").GetString());
    }

    [Fact]
    public void Load_File_Missing_Only_ActiveEnvId_Backfills_Empty_String()
    {
        WriteRaw("""{"globals":{},"environments":[{"id":"e1","name":"Dev"}]}""");

        using var doc = JsonDocument.Parse(EnvironmentStore.Load());
        Assert.True(doc.RootElement.TryGetProperty("activeEnvId", out var active));
        Assert.Equal(JsonValueKind.String, active.ValueKind);
        Assert.Equal(string.Empty, active.GetString());
    }

    [Fact]
    public void Load_Empty_Object_Backfills_All_Three_Properties()
    {
        // Worst-case migration: file is just `{}`. All three props
        // get back-filled in one pass.
        WriteRaw("{}");

        using var doc = JsonDocument.Parse(EnvironmentStore.Load());
        Assert.True(doc.RootElement.TryGetProperty("globals", out _));
        Assert.True(doc.RootElement.TryGetProperty("environments", out _));
        Assert.True(doc.RootElement.TryGetProperty("activeEnvId", out _));
    }

    [Fact]
    public void Load_File_Whose_Root_Is_Not_An_Object_Returns_Empty_Envelope()
    {
        // Someone hand-edited the file to a JSON array. The store
        // refuses to migrate non-object roots and falls back to the
        // empty envelope instead of throwing.
        WriteRaw("""[{"environments":[]}]""");

        using var doc = JsonDocument.Parse(EnvironmentStore.Load());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("environments", out var envs));
        Assert.Equal(0, envs.GetArrayLength());
    }

    [Fact]
    public void Load_File_With_All_Three_Properties_Returns_Verbatim()
    {
        // No migration needed — Load returns the raw file content as
        // a string when all three top-level props are present.
        const string Original = """{"globals":{"k":"v"},"environments":[{"id":"e1"}],"activeEnvId":"e1"}""";
        WriteRaw(Original);

        var loaded = EnvironmentStore.Load();
        Assert.Equal(Original, loaded);
    }

    [Fact]
    public void Load_File_With_All_Three_Plus_Extra_Properties_Preserves_Extras()
    {
        // Forward-compat: a future version of the store might add new
        // top-level props. The current Load() shouldn't drop them.
        const string Original = """{"globals":{},"environments":[],"activeEnvId":"","schemaVersion":2}""";
        WriteRaw(Original);

        var loaded = EnvironmentStore.Load();
        using var doc = JsonDocument.Parse(loaded);
        Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out var sv));
        Assert.Equal(2, sv.GetInt32());
    }

    private void WriteRaw(string json)
    {
        var dir = Path.GetDirectoryName(_tempPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_tempPath, json);
    }
}

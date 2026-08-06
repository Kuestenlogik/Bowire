// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage tests for <see cref="AuthRecordingStore"/> — the id-addressable
/// captured-credential store (#563). Mirrors <c>MockConfigStoreTests</c>:
/// internals are visible via <c>InternalsVisibleTo</c>, single-file cases pin
/// the path via <see cref="AuthRecordingStore.OverrideStorePathForTesting"/>,
/// and multi-file (<c>List</c>) cases write under a per-test temp storageRoot.
/// </summary>
public sealed class AuthRecordingStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AuthRecordingStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bowire-authrec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private static string Rec(string id, string credential, string? scheme = "bearer", string? name = null) =>
        new AuthRecording { Id = id, Credential = credential, Scheme = scheme, Name = name }.ToJson();

    [Fact]
    public void LoadRecording_Missing_File_Returns_Null()
    {
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "missing.json"));
        Assert.Null(AuthRecordingStore.LoadRecording("ws1", null, "r1"));
    }

    [Fact]
    public void Save_Then_Load_Round_Trips()
    {
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "rt.json"));
        AuthRecordingStore.Save("ws1", null, "r1", Rec("r1", "tok-123", "bearer", "Login"));

        var rec = AuthRecordingStore.LoadRecording("ws1", null, "r1");

        Assert.NotNull(rec);
        Assert.Equal("r1", rec!.Id);
        Assert.Equal("tok-123", rec.Credential);
        Assert.Equal("bearer", rec.Scheme);
        Assert.Equal("Login", rec.Name);
    }

    [Fact]
    public void Save_Typed_Overload_Round_Trips()
    {
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "typed.json"));
        AuthRecordingStore.Save("ws1", null, new AuthRecording { Id = "r1", Credential = "k", Scheme = "apikey", Header = "X-API-Key" });

        var rec = AuthRecordingStore.LoadRecording("ws1", null, "r1");
        Assert.Equal("apikey", rec!.Scheme);
        Assert.Equal("X-API-Key", rec.Header);
    }

    [Fact]
    public void LoadRecording_Corrupt_File_Returns_Null()
    {
        var target = Path.Combine(_tempDir, "corrupt.json");
        AuthRecordingStore.OverrideStorePathForTesting(target);
        File.WriteAllText(target, "not-json{");

        Assert.Null(AuthRecordingStore.LoadRecording("ws1", null, "r1"));
    }

    [Fact]
    public void Save_Empty_Payload_Throws()
    {
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "e.json"));
        var ex = Assert.Throws<ArgumentException>(() => AuthRecordingStore.Save("ws1", null, "r1", ""));
        Assert.Equal("json", ex.ParamName);
    }

    [Fact]
    public void Save_Recording_Without_Credential_Throws()
    {
        // A credential-less recording would weaken the gate to presence-only —
        // reject it at the store boundary.
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "nocred.json"));
        var ex = Assert.Throws<ArgumentException>(
            () => AuthRecordingStore.Save("ws1", null, "r1", """{"id":"r1","scheme":"bearer"}"""));
        Assert.Equal("json", ex.ParamName);
    }

    [Fact]
    public void Save_Malformed_Json_Throws_JsonException()
    {
        AuthRecordingStore.OverrideStorePathForTesting(Path.Combine(_tempDir, "bad.json"));
        Assert.ThrowsAny<JsonException>(() => AuthRecordingStore.Save("ws1", null, "r1", "{not json}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("!!!")]
    public void GetStorePath_Empty_Or_Unsafe_Id_Throws(string recordingId)
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        var ex = Assert.Throws<ArgumentException>(() => AuthRecordingStore.GetStorePath("ws1", null, recordingId));
        Assert.Equal("recordingId", ex.ParamName);
    }

    [Fact]
    public void GetStorePath_Strips_Edge_Dots_So_DotDot_Cannot_Escape()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        var path = AuthRecordingStore.GetStorePath("ws1", null, "..r1..");
        Assert.Contains("auth-recordings", path, StringComparison.Ordinal);
        Assert.Contains("r1", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..r1..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_Honours_StorageRoot_Override()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        var storageRoot = Path.Combine(_tempDir, "git-ws");
        Directory.CreateDirectory(storageRoot);

        var path = AuthRecordingStore.GetStorePath("ws1", storageRoot, "r1");

        Assert.StartsWith(storageRoot, path, StringComparison.Ordinal);
        Assert.Contains("auth-recordings", path, StringComparison.Ordinal);
        Assert.EndsWith("r1.json", path, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Returns_Saved_Recordings_Sorted_Ordinally()
    {
        // Multi-file case: no path override, write under a temp storageRoot.
        // Save in REVERSE order so the assertion actually proves the .Sort(),
        // not just insertion/enumeration order.
        AuthRecordingStore.OverrideStorePathForTesting(null);
        AuthRecordingStore.Save("ws1", _tempDir, "beta", Rec("beta", "b-tok", "apikey", "Beta"));
        AuthRecordingStore.Save("ws1", _tempDir, "alpha", Rec("alpha", "a-tok", "bearer", "Alpha"));

        var list = AuthRecordingStore.List("ws1", _tempDir);

        Assert.Equal(2, list.Count);
        Assert.Equal("alpha", list[0].Id);         // sorted ordinally despite reverse save
        Assert.Equal("Alpha", list[0].Name);
        Assert.Equal("bearer", list[0].Scheme);
        Assert.Equal("beta", list[1].Id);
    }

    [Fact]
    public void List_Summary_Does_Not_Leak_The_Credential_Value()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        AuthRecordingStore.Save("ws1", _tempDir, "alpha", Rec("alpha", "super-secret-token", "bearer", "Alpha"));

        // The credential is present in the stored file...
        var stored = File.ReadAllText(AuthRecordingStore.GetStorePath("ws1", _tempDir, "alpha"));
        Assert.Contains("super-secret-token", stored, StringComparison.Ordinal);

        // ...but NOT in the serialized listing projection.
        var list = AuthRecordingStore.List("ws1", _tempDir);
        var listJson = System.Text.Json.JsonSerializer.Serialize(list);
        Assert.DoesNotContain("super-secret-token", listJson, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Missing_Directory_Returns_Empty()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        Assert.Empty(AuthRecordingStore.List("never-saved-ws", _tempDir));
    }

    [Fact]
    public void List_Skips_A_Corrupt_File()
    {
        AuthRecordingStore.OverrideStorePathForTesting(null);
        AuthRecordingStore.Save("ws1", _tempDir, "good", Rec("good", "tok"));
        var dir = AuthRecordingStore.GetStoreDirectory("ws1", _tempDir);
        File.WriteAllText(Path.Combine(dir, "corrupt.json"), "not-json{");

        var list = AuthRecordingStore.List("ws1", _tempDir);

        var only = Assert.Single(list);
        Assert.Equal("good", only.Id);
    }
}

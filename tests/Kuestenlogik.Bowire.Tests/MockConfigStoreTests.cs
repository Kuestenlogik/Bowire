// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage tests for <see cref="MockConfigStore"/> — the on-disk
/// per-(workspace, mock) mock-configuration store (#558). Mirrors
/// <c>PresetStoreCoverageTests</c>; internals are visible via the
/// <c>InternalsVisibleTo</c> entry, and writes are redirected into a
/// per-test temp directory via
/// <see cref="MockConfigStore.OverrideStorePathForTesting"/>.
/// </summary>
public sealed class MockConfigStoreTests : IDisposable
{
    private readonly string _tempDir;

    public MockConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bowire-mockcfg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        MockConfigStore.OverrideStorePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_Missing_File_Returns_Default_Envelope()
    {
        var target = Path.Combine(_tempDir, "missing.json");
        MockConfigStore.OverrideStorePathForTesting(target);

        var json = MockConfigStore.Load("ws1", null, "m1");

        // A well-formed, empty MockConfiguration at the current version.
        var config = MockConfiguration.Parse(json);
        Assert.Equal(MockConfiguration.CurrentFormatVersion, config.ConfigFormatVersion);
        Assert.Empty(config.FieldOverrides);
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Document_Verbatim()
    {
        var target = Path.Combine(_tempDir, "roundtrip.json");
        MockConfigStore.OverrideStorePathForTesting(target);
        const string Payload = """{"configFormatVersion":1,"fieldOverrides":[{"jsonPath":"$.status","value":"ok"}]}""";

        MockConfigStore.Save("ws1", null, "m1", Payload);
        var loaded = MockConfigStore.Load("ws1", null, "m1");

        Assert.Equal(Payload, loaded);
    }

    [Fact]
    public void LoadConfig_Returns_Typed_View()
    {
        var target = Path.Combine(_tempDir, "typed.json");
        MockConfigStore.OverrideStorePathForTesting(target);
        MockConfigStore.Save("ws1", null, "m1",
            """{"fieldOverrides":[{"service":"Orders","method":"list","jsonPath":"$.total","value":5}]}""");

        var config = MockConfigStore.LoadConfig("ws1", null, "m1");

        var ov = Assert.Single(config.FieldOverrides);
        Assert.Equal("Orders", ov.Service);
        Assert.Equal("$.total", ov.JsonPath);
        Assert.Equal(5, ov.Value!.Value.GetInt32());
    }

    [Fact]
    public void Load_Corrupt_File_Returns_Default_Envelope()
    {
        var target = Path.Combine(_tempDir, "corrupt.json");
        MockConfigStore.OverrideStorePathForTesting(target);
        File.WriteAllText(target, "not-json-at-all{");

        var json = MockConfigStore.Load("ws1", null, "m1");

        // Never throws — falls back to a parseable default envelope.
        Assert.Equal(MockConfiguration.CurrentFormatVersion,
            MockConfiguration.Parse(json).ConfigFormatVersion);
    }

    [Fact]
    public void Load_Empty_File_Returns_Default_Envelope()
    {
        var target = Path.Combine(_tempDir, "empty-file.json");
        MockConfigStore.OverrideStorePathForTesting(target);
        File.WriteAllText(target, string.Empty);

        var json = MockConfigStore.Load("ws1", null, "m1");

        // The zero-byte file must fall back to the default envelope (never the
        // raw ""): assert on the returned document's shape, not just that Parse
        // returns non-null (which it always does).
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var config = MockConfiguration.Parse(json);
        Assert.Equal(MockConfiguration.CurrentFormatVersion, config.ConfigFormatVersion);
        Assert.Empty(config.FieldOverrides);
    }

    [Fact]
    public void Save_Empty_Payload_Throws_ArgumentException()
    {
        var target = Path.Combine(_tempDir, "empty.json");
        MockConfigStore.OverrideStorePathForTesting(target);

        var ex = Assert.Throws<ArgumentException>(() => MockConfigStore.Save("ws1", null, "m1", ""));
        Assert.Equal("json", ex.ParamName);
    }

    [Fact]
    public void Save_Malformed_Json_Throws_JsonException()
    {
        var target = Path.Combine(_tempDir, "bad.json");
        MockConfigStore.OverrideStorePathForTesting(target);

        Assert.ThrowsAny<JsonException>(() => MockConfigStore.Save("ws1", null, "m1", "{not json}"));
    }

    [Fact]
    public void Save_Non_Object_Json_Throws()
    {
        // A JSON array is valid JSON but not a MockConfiguration — Parse rejects it.
        var target = Path.Combine(_tempDir, "array.json");
        MockConfigStore.OverrideStorePathForTesting(target);

        Assert.ThrowsAny<JsonException>(() => MockConfigStore.Save("ws1", null, "m1", "[1,2,3]"));
    }

    [Fact]
    public void Save_Creates_Parent_Directory_When_Missing()
    {
        var nested = Path.Combine(_tempDir, "deep", "deeper", "m.json");
        MockConfigStore.OverrideStorePathForTesting(nested);

        MockConfigStore.Save("ws1", null, "m1", "{}");

        Assert.True(File.Exists(nested));
    }

    [Theory]
    [InlineData("m1")]
    [InlineData("mock-with-dash")]
    [InlineData("mock_underscore")]
    [InlineData("ABC123")]
    [InlineData("ws_1a2b.orders")]
    public void GetStorePath_Accepts_Safe_Mock_Ids(string mockId)
    {
        MockConfigStore.OverrideStorePathForTesting(null);

        var path = MockConfigStore.GetStorePath("ws1", null, mockId);

        Assert.Contains("mocks", path, StringComparison.Ordinal);
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("!!!")]
    public void GetStorePath_Empty_Or_All_Unsafe_Mock_Id_Throws(string mockId)
    {
        MockConfigStore.OverrideStorePathForTesting(null);

        var ex = Assert.Throws<ArgumentException>(() => MockConfigStore.GetStorePath("ws1", null, mockId));
        Assert.Equal("mockId", ex.ParamName);
    }

    [Fact]
    public void GetStorePath_Sanitises_Unsafe_Chars_In_Mock_Id()
    {
        MockConfigStore.OverrideStorePathForTesting(null);

        var path = MockConfigStore.GetStorePath("ws1", null, "m:1 orders");

        Assert.Contains("m1orders", path, StringComparison.Ordinal);
        Assert.DoesNotContain("m:1", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_Strips_Edge_Dots_So_DotDot_Cannot_Escape()
    {
        MockConfigStore.OverrideStorePathForTesting(null);

        var path = MockConfigStore.GetStorePath("ws1", null, "..m1..");

        Assert.Contains("m1", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..m1..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_Honours_StorageRoot_Override()
    {
        MockConfigStore.OverrideStorePathForTesting(null);
        var storageRoot = Path.Combine(_tempDir, "git-workspace");
        Directory.CreateDirectory(storageRoot);

        var path = MockConfigStore.GetStorePath("ws1", storageRoot, "m1");

        Assert.StartsWith(storageRoot, path, StringComparison.Ordinal);
        Assert.Contains("mocks", path, StringComparison.Ordinal);
        Assert.EndsWith("m1.json", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_Overwrites_Existing_File()
    {
        var target = Path.Combine(_tempDir, "overwrite.json");
        MockConfigStore.OverrideStorePathForTesting(target);

        MockConfigStore.Save("ws1", null, "m1", """{"source":{"kind":"openapi","path":"a"}}""");
        MockConfigStore.Save("ws1", null, "m1", """{"source":{"kind":"graphql","path":"b"}}""");

        var json = MockConfigStore.Load("ws1", null, "m1");
        Assert.Contains("graphql", json, StringComparison.Ordinal);
        Assert.DoesNotContain("openapi", json, StringComparison.Ordinal);
    }
}

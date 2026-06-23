// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage tests for <see cref="PresetStore"/> — the on-disk
/// per-(workspace, mode) preset store. Internals are visible via the
/// <c>InternalsVisibleTo</c> entry on the production csproj; tests use
/// the <see cref="PresetStore.OverrideStorePathForTesting"/> hook so
/// writes land in a per-test temp directory rather than the developer's
/// real <c>~/.bowire/</c>.
/// </summary>
public sealed class PresetStoreCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public PresetStoreCoverageTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"bowire-preset-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Clear any test-override that survived a partial test failure
        // so the next fixture starts clean.
        PresetStore.OverrideStorePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_Missing_File_Returns_Empty_Array_Envelope()
    {
        var target = Path.Combine(_tempDir, "presets-missing.json");
        PresetStore.OverrideStorePathForTesting(target);

        var json = PresetStore.Load("ws1", null, "discover");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Document_Verbatim()
    {
        var target = Path.Combine(_tempDir, "presets-roundtrip.json");
        PresetStore.OverrideStorePathForTesting(target);
        const string Payload = """[{"id":"p1","name":"Login"}]""";

        PresetStore.Save("ws1", null, "discover", Payload);
        var loaded = PresetStore.Load("ws1", null, "discover");

        Assert.Equal(Payload, loaded);
    }

    [Fact]
    public void Load_Corrupt_File_Returns_Empty_Envelope()
    {
        var target = Path.Combine(_tempDir, "presets-corrupt.json");
        PresetStore.OverrideStorePathForTesting(target);
        File.WriteAllText(target, "not-json-at-all{");

        var json = PresetStore.Load("ws1", null, "discover");

        Assert.Equal("[]", json);
    }

    [Fact]
    public void Save_Empty_Payload_Throws_ArgumentException()
    {
        var target = Path.Combine(_tempDir, "presets-empty.json");
        PresetStore.OverrideStorePathForTesting(target);

        var ex = Assert.Throws<ArgumentException>(
            () => PresetStore.Save("ws1", null, "discover", ""));
        Assert.Equal("json", ex.ParamName);
    }

    [Fact]
    public void Save_Whitespace_Payload_Throws_ArgumentException()
    {
        var target = Path.Combine(_tempDir, "presets-ws.json");
        PresetStore.OverrideStorePathForTesting(target);

        Assert.Throws<ArgumentException>(
            () => PresetStore.Save("ws1", null, "discover", "   "));
    }

    [Fact]
    public void Save_Malformed_Json_Throws_JsonException()
    {
        var target = Path.Combine(_tempDir, "presets-bad.json");
        PresetStore.OverrideStorePathForTesting(target);

        Assert.ThrowsAny<JsonException>(
            () => PresetStore.Save("ws1", null, "discover", "{not json}"));
    }

    [Fact]
    public void Save_Creates_Parent_Directory_When_Missing()
    {
        // Point at a nested path whose parent dir doesn't exist —
        // PresetStore.Save should call Directory.CreateDirectory.
        var nested = Path.Combine(_tempDir, "deep", "deeper", "p.json");
        PresetStore.OverrideStorePathForTesting(nested);

        PresetStore.Save("ws1", null, "discover", "[]");

        Assert.True(File.Exists(nested));
    }

    [Theory]
    [InlineData("discover")]
    [InlineData("flows")]
    [InlineData("benchmarks")]
    [InlineData("mocks")]
    [InlineData("proxy")]
    [InlineData("security")]
    [InlineData("with-dash")]
    [InlineData("with_underscore")]
    [InlineData("ABC123")]
    public void GetStorePath_Accepts_Known_Mode_Slugs(string mode)
    {
        // Without the override, GetStorePath flows through the real
        // sanitiser → BowireUserContext.GetWorkspacePath. Successful
        // return = the sanitiser allowed the mode.
        PresetStore.OverrideStorePathForTesting(null);

        var path = PresetStore.GetStorePath("ws1", null, mode);

        Assert.Contains("presets", path, StringComparison.Ordinal);
        Assert.Contains(mode, path, StringComparison.Ordinal);
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void GetStorePath_Empty_Or_Whitespace_Mode_Throws(string mode)
    {
        PresetStore.OverrideStorePathForTesting(null);

        var ex = Assert.Throws<ArgumentException>(
            () => PresetStore.GetStorePath("ws1", null, mode));
        Assert.Equal("mode", ex.ParamName);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData("with.dot")]
    [InlineData("with!bang")]
    [InlineData("../escape")]
    public void GetStorePath_Mode_With_Unsafe_Chars_Throws(string mode)
    {
        PresetStore.OverrideStorePathForTesting(null);

        var ex = Assert.Throws<ArgumentException>(
            () => PresetStore.GetStorePath("ws1", null, mode));
        Assert.Equal("mode", ex.ParamName);
    }

    [Fact]
    public void GetStorePath_Honours_Override_Regardless_Of_Inputs()
    {
        var customPath = Path.Combine(_tempDir, "custom-override.json");
        PresetStore.OverrideStorePathForTesting(customPath);

        // Override short-circuits sanitisation — even "would normally
        // throw" inputs return the override.
        Assert.Equal(customPath, PresetStore.GetStorePath("ws1", null, "discover"));
        Assert.Equal(customPath, PresetStore.GetStorePath("any", "/storage", "flows"));
    }

    [Fact]
    public void GetStorePath_Empty_WorkspaceId_Falls_Back_To_Anon_Or_Empty()
    {
        // Empty workspaceId short-circuits the sanitiser branch and
        // composes the path without a workspace segment — the default
        // BowireUserContext implementation handles that case.
        PresetStore.OverrideStorePathForTesting(null);

        var path = PresetStore.GetStorePath("", null, "discover");

        Assert.Contains("presets", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_Sanitises_WorkspaceId_With_Unsafe_Chars()
    {
        // ":" and " " are stripped by the sanitiser; the remaining
        // letters form a valid slug. The path includes the sanitised
        // form, NOT the raw input. (On Windows the absolute path's
        // drive letter portion has its own colon — only check the
        // segment that derives from the workspaceId is clean.)
        PresetStore.OverrideStorePathForTesting(null);

        var path = PresetStore.GetStorePath("ws:1 alpha", null, "discover");

        Assert.Contains("ws1alpha", path, StringComparison.Ordinal);
        // The raw workspaceId must not survive into the path verbatim.
        Assert.DoesNotContain("ws:1", path, StringComparison.Ordinal);
        Assert.DoesNotContain("1 alpha", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_Sanitises_Leading_And_Trailing_Dots_In_WorkspaceId()
    {
        // Leading + trailing dots are stripped so ".." can't escape
        // upward through the workspaceId segment.
        PresetStore.OverrideStorePathForTesting(null);

        var path = PresetStore.GetStorePath("..ws1..", null, "discover");

        // After stripping the outer dots we get "ws1".
        Assert.Contains("ws1", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..ws1..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStorePath_WorkspaceId_Of_All_Dots_Falls_Back_To_Anon()
    {
        // Stripping leading + trailing dots yields the empty string;
        // the sanitiser substitutes "anon" so the file system never
        // sees an empty segment.
        PresetStore.OverrideStorePathForTesting(null);

        var path = PresetStore.GetStorePath("....", null, "discover");

        Assert.Contains("anon", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_Locks_Concurrent_Callers_Without_Throwing()
    {
        // The store uses a single static lock so back-to-back saves
        // from different threads serialise safely.
        var target = Path.Combine(_tempDir, "presets-concurrent.json");
        PresetStore.OverrideStorePathForTesting(target);

        var threads = new List<Thread>();
        for (var i = 0; i < 8; i++)
        {
            var idx = i;
            threads.Add(new Thread(() =>
                PresetStore.Save("ws1", null, "discover", $"[{idx}]")));
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // The last writer wins but the file is well-formed JSON.
        var json = PresetStore.Load("ws1", null, "discover");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Save_Overwrites_Existing_File()
    {
        var target = Path.Combine(_tempDir, "presets-overwrite.json");
        PresetStore.OverrideStorePathForTesting(target);

        PresetStore.Save("ws1", null, "discover", """[{"id":"a"}]""");
        PresetStore.Save("ws1", null, "discover", """[{"id":"b"}]""");

        var json = PresetStore.Load("ws1", null, "discover");
        Assert.Contains("\"id\":\"b\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"a\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_Accepts_Empty_Array_Payload()
    {
        var target = Path.Combine(_tempDir, "presets-empty-array.json");
        PresetStore.OverrideStorePathForTesting(target);

        PresetStore.Save("ws1", null, "discover", "[]");

        Assert.Equal("[]", File.ReadAllText(target));
    }

    [Fact]
    public void Load_Empty_File_Returns_Empty_Envelope()
    {
        // A zero-byte file fails JsonDocument.Parse — the Load path
        // catches the exception and falls back to the empty literal.
        var target = Path.Combine(_tempDir, "presets-empty-file.json");
        PresetStore.OverrideStorePathForTesting(target);
        File.WriteAllText(target, string.Empty);

        Assert.Equal("[]", PresetStore.Load("ws1", null, "discover"));
    }

    [Fact]
    public void GetStorePath_Honours_StorageRoot_Override()
    {
        // When storageRoot is set, BowireUserContext anchors the path
        // under that root instead of the user's home directory.
        PresetStore.OverrideStorePathForTesting(null);
        var storageRoot = Path.Combine(_tempDir, "git-workspace");
        Directory.CreateDirectory(storageRoot);

        var path = PresetStore.GetStorePath("ws1", storageRoot, "discover");

        Assert.StartsWith(storageRoot, path, StringComparison.Ordinal);
        Assert.Contains("presets", path, StringComparison.Ordinal);
        Assert.EndsWith("discover.json", path, StringComparison.Ordinal);
    }
}

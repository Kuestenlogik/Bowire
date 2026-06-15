// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Workspace.Git;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Behavioural coverage for <see cref="FileEntityStore"/> — the
/// per-entity file reader/writer the git-backed workspace runtime
/// routes through (#196 Phase 2.2).
///
/// <para>
/// Each test creates a fresh temp subdirectory so parallel runs and
/// post-test cleanup don't cross streams. Asserts hit concrete file
/// contents + JSON-deserialised shapes, not just "didn't throw".
/// </para>
/// </summary>
public sealed class FileEntityStoreTests : IDisposable
{
    // Stable expected-id sequences pulled out into static readonly
    // fields so CA1861 ("avoid constant array arguments") doesn't trip
    // on the per-test allocations. The Workspace.Git tests project
    // promotes that analyzer to an error.
    private static readonly string[] ExpectedSortedAlphaMangoZebra = ["alpha", "mango", "zebra"];
    private static readonly string[] ExpectedCollectionsAB = ["col_a", "col_b"];

    private readonly string _root;

    public FileEntityStoreTests()
    {
        _root = Directory.CreateTempSubdirectory("bowire-git-store-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup — temp will reclaim eventually */ }
    }

    // ----------------------------------------------------------------
    // Construction + validation
    // ----------------------------------------------------------------

    [Fact]
    public void Ctor_rejects_null_or_whitespace_root()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws
        // ArgumentNullException for null and ArgumentException for the
        // empty/whitespace branch — both inherit from ArgumentException,
        // so ThrowsAny<ArgumentException> covers the contract without
        // pinning a specific subclass.
        Assert.ThrowsAny<ArgumentException>(() => new FileEntityStore(null!));
        Assert.ThrowsAny<ArgumentException>(() => new FileEntityStore(""));
        Assert.ThrowsAny<ArgumentException>(() => new FileEntityStore("   "));
    }

    [Fact]
    public void Ctor_normalises_relative_path_to_absolute()
    {
        // The store keeps a normalised absolute root so subsequent file
        // operations don't surprise the caller when their cwd changes.
        var rel = Path.GetRelativePath(Environment.CurrentDirectory, _root);
        var sut = new FileEntityStore(rel);
        Assert.Equal(_root, sut.StorageRoot);
    }

    [Theory]
    [InlineData("environment")] // typo: singular
    [InlineData("Collections")] // typo: case
    [InlineData("bodies")]      // unknown bucket
    [InlineData("")]
    public async Task Unknown_entity_kind_is_rejected_with_argument_exception(string kind)
    {
        var sut = new FileEntityStore(_root);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ListAsync(kind, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.LoadAsync(kind, "any", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.SaveAsync(kind, "any", "{}", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.DeleteAsync(kind, "any", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/id")]
    [InlineData("back\\slash")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Entity_id_path_traversal_attempts_rejected(string id)
    {
        var sut = new FileEntityStore(_root);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.SaveAsync("environments", id, "{}", TestContext.Current.CancellationToken));
    }

    // ----------------------------------------------------------------
    // Round-trip per simple entity kind
    // ----------------------------------------------------------------

    public static TheoryData<string> SimpleKinds() => new()
    {
        "environments",
        "recordings",
        "scripts",
        "flows",
    };

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task SaveAsync_creates_per_kind_directory_on_first_write(string kind)
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync(kind, "env_alpha", """{"id":"env_alpha","name":"Alpha"}""",
            TestContext.Current.CancellationToken);

        var file = Path.Combine(_root, kind, "env_alpha.json");
        Assert.True(File.Exists(file));
        var written = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        // Indented format with the bundle store's shape.
        Assert.Contains("\n", written);
        // Trailing newline so PR diffs land cleanly.
        Assert.EndsWith(Environment.NewLine, written);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("env_alpha", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Alpha", doc.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task LoadAsync_returns_null_for_missing_entity(string kind)
    {
        var sut = new FileEntityStore(_root);
        var hit = await sut.LoadAsync(kind, "never_authored",
            TestContext.Current.CancellationToken);
        Assert.Null(hit);
    }

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task LoadAsync_round_trips_a_previously_saved_entity(string kind)
    {
        var sut = new FileEntityStore(_root);
        var raw = """{"id":"a","label":"value","nested":{"k":1}}""";
        await sut.SaveAsync(kind, "a", raw, TestContext.Current.CancellationToken);

        var loaded = await sut.LoadAsync(kind, "a", TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        using var doc = JsonDocument.Parse(loaded!);
        Assert.Equal("a", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("value", doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("nested").GetProperty("k").GetInt32());
    }

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task ListAsync_returns_empty_when_bucket_missing(string kind)
    {
        // No bucket directory created yet — list should not throw.
        var sut = new FileEntityStore(_root);
        var ids = await sut.ListAsync(kind, TestContext.Current.CancellationToken);
        Assert.Empty(ids);
    }

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task ListAsync_returns_every_saved_id_sorted(string kind)
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync(kind, "zebra", """{"id":"zebra"}""", TestContext.Current.CancellationToken);
        await sut.SaveAsync(kind, "alpha", """{"id":"alpha"}""", TestContext.Current.CancellationToken);
        await sut.SaveAsync(kind, "mango", """{"id":"mango"}""", TestContext.Current.CancellationToken);

        var ids = await sut.ListAsync(kind, TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedSortedAlphaMangoZebra, ids);
    }

    [Theory]
    [MemberData(nameof(SimpleKinds))]
    public async Task DeleteAsync_removes_the_file_idempotently(string kind)
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync(kind, "x", """{"id":"x"}""", TestContext.Current.CancellationToken);

        await sut.DeleteAsync(kind, "x", TestContext.Current.CancellationToken);
        Assert.Null(await sut.LoadAsync(kind, "x", TestContext.Current.CancellationToken));

        // Idempotent — re-deleting a missing entity must not throw.
        await sut.DeleteAsync(kind, "x", TestContext.Current.CancellationToken);
        await sut.DeleteAsync(kind, "never_existed", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_json_input()
    {
        // The store re-serialises through JsonDocument.Parse, so garbage
        // input gets rejected early instead of silently landing on disk.
        // The concrete System.Text.Json exception is JsonReaderException
        // (derives from JsonException); ThrowsAnyAsync<JsonException>
        // covers both without pinning the parser internals.
        var sut = new FileEntityStore(_root);
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            sut.SaveAsync("environments", "bad", "{ not json",
                TestContext.Current.CancellationToken));
        // Nothing on disk for the rejected write.
        Assert.False(File.Exists(Path.Combine(_root, "environments", "bad.json")));
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_entity_content()
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync("environments", "e", """{"id":"e","v":1}""",
            TestContext.Current.CancellationToken);
        await sut.SaveAsync("environments", "e", """{"id":"e","v":2}""",
            TestContext.Current.CancellationToken);

        var loaded = await sut.LoadAsync("environments", "e",
            TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        using var doc = JsonDocument.Parse(loaded!);
        Assert.Equal(2, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_persists_indented_camelCase_shape()
    {
        // Pins the bundle-store-compatible shape: indented JSON, with
        // camelCase property names (which we preserve verbatim — the
        // caller hands us the workbench's canonical document).
        var sut = new FileEntityStore(_root);
        var minified = """{"id":"e","camelCased":{"nestedKey":"v"}}""";
        await sut.SaveAsync("environments", "e", minified,
            TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(
            Path.Combine(_root, "environments", "e.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("\"camelCased\"", written);
        Assert.Contains("\"nestedKey\"", written);
        // Indentation introduces whitespace before nested keys.
        Assert.Matches(@"\n\s+""camelCased""", written);
    }

    // ----------------------------------------------------------------
    // Collections — per-request file fan-out
    // ----------------------------------------------------------------

    [Fact]
    public async Task Collections_save_creates_subdir_with_container_and_per_request_files()
    {
        var sut = new FileEntityStore(_root);
        var collection = """
        {
            "id": "col_payments",
            "name": "Payments",
            "requests": [
                { "id": "req_list",   "method": "GET",  "path": "/payments" },
                { "id": "req_create", "method": "POST", "path": "/payments" }
            ]
        }
        """;
        await sut.SaveAsync("collections", "col_payments", collection,
            TestContext.Current.CancellationToken);

        var dir = Path.Combine(_root, "collections", "col_payments");
        Assert.True(Directory.Exists(dir));

        var container = Path.Combine(dir, "col_payments.json");
        Assert.True(File.Exists(container));
        using (var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(container, TestContext.Current.CancellationToken)))
        {
            Assert.Equal("Payments", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("requests").GetArrayLength());
        }

        var listReq = Path.Combine(dir, "req_list.req.json");
        Assert.True(File.Exists(listReq));
        using (var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(listReq, TestContext.Current.CancellationToken)))
        {
            Assert.Equal("GET", doc.RootElement.GetProperty("method").GetString());
            Assert.Equal("/payments", doc.RootElement.GetProperty("path").GetString());
        }

        var createReq = Path.Combine(dir, "req_create.req.json");
        Assert.True(File.Exists(createReq));
        using (var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(createReq, TestContext.Current.CancellationToken)))
        {
            Assert.Equal("POST", doc.RootElement.GetProperty("method").GetString());
        }
    }

    [Fact]
    public async Task Collections_save_without_requests_still_round_trips()
    {
        // A collection with no requests array should round-trip as a
        // container-only document, no .req.json siblings.
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync("collections", "col_empty",
            """{"id":"col_empty","name":"Empty"}""",
            TestContext.Current.CancellationToken);

        var dir = Path.Combine(_root, "collections", "col_empty");
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "col_empty.json")));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.req.json"));

        var loaded = await sut.LoadAsync("collections", "col_empty",
            TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        using var doc = JsonDocument.Parse(loaded!);
        Assert.Equal("Empty", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Collections_save_resync_drops_stale_request_files()
    {
        // Saving a collection where a request was removed should drop
        // the corresponding .req.json — otherwise stale files leak into
        // PR diffs.
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync("collections", "col_x", """
        {"id":"col_x","requests":[
            {"id":"req_a"},
            {"id":"req_b"}
        ]}
        """, TestContext.Current.CancellationToken);

        var dir = Path.Combine(_root, "collections", "col_x");
        Assert.True(File.Exists(Path.Combine(dir, "req_a.req.json")));
        Assert.True(File.Exists(Path.Combine(dir, "req_b.req.json")));

        // Resync with req_a removed.
        await sut.SaveAsync("collections", "col_x", """
        {"id":"col_x","requests":[{"id":"req_b"}]}
        """, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(dir, "req_a.req.json")));
        Assert.True(File.Exists(Path.Combine(dir, "req_b.req.json")));
    }

    [Fact]
    public async Task Collections_list_returns_each_collection_once()
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync("collections", "col_a",
            """{"id":"col_a","requests":[{"id":"r1"}]}""",
            TestContext.Current.CancellationToken);
        await sut.SaveAsync("collections", "col_b",
            """{"id":"col_b"}""",
            TestContext.Current.CancellationToken);

        var ids = await sut.ListAsync("collections", TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedCollectionsAB, ids);
    }

    [Fact]
    public async Task Collections_delete_removes_subdir_and_top_level_variant()
    {
        var sut = new FileEntityStore(_root);
        await sut.SaveAsync("collections", "col_full",
            """{"id":"col_full","requests":[{"id":"r"}]}""",
            TestContext.Current.CancellationToken);

        // Simulate a legacy top-level-file collection that hasn't been
        // re-saved through the new layout yet — the delete should pick
        // it up too.
        var topLevel = Path.Combine(_root, "collections", "col_legacy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(topLevel)!);
        await File.WriteAllTextAsync(topLevel, """{"id":"col_legacy"}""",
            TestContext.Current.CancellationToken);

        await sut.DeleteAsync("collections", "col_full", TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(_root, "collections", "col_full")));

        await sut.DeleteAsync("collections", "col_legacy", TestContext.Current.CancellationToken);
        Assert.False(File.Exists(topLevel));
    }

    // ----------------------------------------------------------------
    // Concurrency (best-effort) — concurrent writes to distinct ids
    // ----------------------------------------------------------------

    [Fact]
    public async Task Concurrent_writes_to_distinct_ids_all_land_on_disk()
    {
        var sut = new FileEntityStore(_root);
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, 32).Select(i =>
            sut.SaveAsync("environments", $"env_{i:00}",
                $$"""{"id":"env_{{i:00}}","v":{{i}}}""", ct)).ToArray();
        await Task.WhenAll(tasks);

        var ids = await sut.ListAsync("environments", ct);
        Assert.Equal(32, ids.Count);
        // Spot-check a couple — each entity should carry its own value.
        var loaded5 = await sut.LoadAsync("environments", "env_05", ct);
        Assert.NotNull(loaded5);
        using var doc = JsonDocument.Parse(loaded5!);
        Assert.Equal(5, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Storage_root_does_not_need_to_exist_before_first_write()
    {
        // The store can target a not-yet-materialised path so the
        // workbench's "save environment" path doesn't have to scaffold
        // the layout first.
        var nested = Path.Combine(_root, "nested", "ws_root");
        var sut = new FileEntityStore(nested);
        await sut.SaveAsync("environments", "e", """{"id":"e"}""",
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(nested, "environments", "e.json")));
    }
}

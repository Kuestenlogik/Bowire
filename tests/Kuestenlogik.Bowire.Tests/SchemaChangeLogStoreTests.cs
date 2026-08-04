// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit coverage for <see cref="SchemaChangeLogStore"/> (#185) — the
/// disk-backed per-workspace schema-change log behind the statusbar
/// pill. Every test pins the store to its own temp file via
/// <see cref="SchemaChangeLogStore.OverrideStorePathForTesting"/>, so
/// nothing touches the developer's real <c>~/.bowire</c> tree and no
/// serialisation collection is needed (the store is an instance, not
/// a static).
/// </summary>
public sealed class SchemaChangeLogStoreTests : IDisposable
{
    private readonly SchemaChangeLogStore _store = new();
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "bowire-schemachange-" + Guid.NewGuid().ToString("N"), "log.json");

    public SchemaChangeLogStoreTests()
    {
        _store.OverrideStorePathForTesting(_path);
    }

    public void Dispose()
    {
        _store.OverrideStorePathForTesting(null);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
    }

    private static SchemaChangeEntry Entry(
        string type, string service, string? method = null, DateTimeOffset? at = null)
        => new(at ?? DateTimeOffset.UtcNow, type, service) { Method = method };

    private void WriteLogFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
    }

    [Fact]
    public void Load_missing_file_returns_empty_envelope()
    {
        var envelope = _store.Load("ws1", null);
        Assert.Empty(envelope.Entries);
        Assert.Null(envelope.LastReadAt);
    }

    [Fact]
    public void Append_then_load_round_trips_the_entries()
    {
        _store.Append("ws1", null,
        [
            Entry("added", "Orders", "Orders/Cancel"),
            Entry("signature", "Orders", "GET /orders") with { Detail = "request shape changed" },
        ]);

        var envelope = _store.Load("ws1", null);
        Assert.Equal(2, envelope.Entries.Count);
        Assert.Equal("added", envelope.Entries[0].Type);
        Assert.Equal("Orders/Cancel", envelope.Entries[0].Method);
        Assert.Equal("request shape changed", envelope.Entries[1].Detail);
        Assert.True(File.Exists(_path), "the log file must exist after an append");
    }

    [Fact]
    public void Append_stamps_every_entry_with_the_server_clock()
    {
        // One clock authority: retention, the cap and the unread
        // compare all use At, and a skewed browser clock would
        // otherwise produce entries born read or unreadable forever.
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        _store.Append("ws1", null,
            [Entry("added", "Orders", at: DateTimeOffset.UtcNow.AddDays(2))]);
        var stamp = Assert.Single(_store.Load("ws1", null).Entries).At;
        Assert.True(stamp >= before && stamp <= DateTimeOffset.UtcNow.AddMinutes(1),
            "the client's (future-dated) stamp must be replaced with server-now");
    }

    [Fact]
    public void Append_rejects_an_unknown_change_type_without_touching_disk()
    {
        Assert.Throws<ArgumentException>(() =>
            _store.Append("ws1", null, [Entry("exploded", "Orders")]));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Append_rejects_a_missing_service()
    {
        Assert.Throws<ArgumentException>(() =>
            _store.Append("ws1", null, [Entry("added", "  ")]));
    }

    [Fact]
    public void Append_rejects_a_null_entry_as_argument_not_500()
    {
        // A JSON `null` array element deserialises as a null entry —
        // the store must turn that into the ArgumentException → 400
        // path, not a NullReferenceException → 500.
        Assert.Throws<ArgumentException>(() =>
            _store.Append("ws1", null, [Entry("added", "Orders"), null]));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Append_truncates_oversized_strings()
    {
        _store.Append("ws1", null,
            [Entry("signature", new string('s', 900), new string('m', 900))
                with { Detail = new string('d', 9000) }]);
        var kept = Assert.Single(_store.Load("ws1", null).Entries);
        Assert.Equal(SchemaChangeLogStore.MaxNameLength, kept.Service.Length);
        Assert.Equal(SchemaChangeLogStore.MaxNameLength, kept.Method!.Length);
        Assert.Equal(SchemaChangeLogStore.MaxDetailLength, kept.Detail!.Length);
    }

    [Fact]
    public void Append_collapses_the_same_transition_observed_by_two_clients()
    {
        // Two tabs watching the same workspace both post the delta.
        var entry = Entry("signature", "Orders", "GET /orders") with { Detail = "request shape changed" };
        _store.Append("ws1", null, [entry]);
        var envelope = _store.Append("ws1", null, [entry with { At = default }]);
        Assert.Single(envelope.Entries);

        // A genuinely different change is NOT collapsed.
        envelope = _store.Append("ws1", null, [entry with { Detail = "response shape changed" }]);
        Assert.Equal(2, envelope.Entries.Count);
    }

    [Fact]
    public void Entries_older_than_the_retention_window_are_pruned_on_load()
    {
        // Retention is a property of stored history — write the file
        // directly (Append would re-stamp the old date away).
        WriteLogFile(
            $$"""
            { "entries": [
                { "at": "{{DateTimeOffset.UtcNow.AddDays(-(SchemaChangeLogStore.RetentionDays + 1)):O}}", "type": "added", "service": "Old" },
                { "at": "{{DateTimeOffset.UtcNow.AddDays(-1):O}}", "type": "added", "service": "Fresh" }
            ] }
            """);
        var kept = Assert.Single(_store.Load("ws1", null).Entries);
        Assert.Equal("Fresh", kept.Service);
    }

    [Fact]
    public void Future_dated_entries_from_a_hand_edited_file_are_dropped()
    {
        // Pre-server-stamp files (or a hand edit) can carry future
        // dates, which would never age out, always count unread, and
        // sort newest under the cap. The prune ceiling removes them.
        WriteLogFile(
            $$"""
            { "entries": [
                { "at": "{{DateTimeOffset.UtcNow.AddYears(5):O}}", "type": "added", "service": "FromTheFuture" },
                { "at": "{{DateTimeOffset.UtcNow.AddDays(-1):O}}", "type": "added", "service": "Fresh" }
            ] }
            """);
        var kept = Assert.Single(_store.Load("ws1", null).Entries);
        Assert.Equal("Fresh", kept.Service);
    }

    [Fact]
    public void The_entry_cap_drops_the_oldest_first()
    {
        var batch = Enumerable.Range(0, SchemaChangeLogStore.MaxEntries + 5)
            .Select(i => (SchemaChangeEntry?)Entry("added", "Svc" + i))
            .ToList();
        var envelope = _store.Append("ws1", null, batch);

        Assert.Equal(SchemaChangeLogStore.MaxEntries, envelope.Entries.Count);
        Assert.Equal("Svc5", envelope.Entries[0].Service);
    }

    [Fact]
    public void MarkRead_moves_the_watermark()
    {
        _store.Append("ws1", null, [Entry("added", "Orders")]);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var envelope = _store.MarkRead("ws1", null);
        Assert.NotNull(envelope.LastReadAt);
        Assert.True(envelope.LastReadAt > before);

        Assert.Equal(envelope.LastReadAt, _store.Load("ws1", null).LastReadAt);
    }

    [Fact]
    public void Load_corrupt_file_falls_back_to_the_empty_envelope()
    {
        WriteLogFile("{ this is not valid json");
        Assert.Empty(_store.Load("ws1", null).Entries);
    }

    [Fact]
    public void Load_bends_valid_json_with_the_wrong_shape_back_into_shape()
    {
        // A hand-edited or merge-conflicted git-backed log can parse
        // fine and still violate the shape — the workspace must not
        // wedge into permanent 400s/500s over it.
        WriteLogFile("""{ "entries": null }""");
        Assert.Empty(_store.Load("ws1", null).Entries);

        WriteLogFile(
            $$"""
            { "entries": [ null, { "at": "{{DateTimeOffset.UtcNow.AddHours(-1):O}}", "type": "added", "service": "Orders" } ] }
            """);
        var kept = Assert.Single(_store.Load("ws1", null).Entries);
        Assert.Equal("Orders", kept.Service);

        // And a follow-up append over the weird file must work, not NRE.
        var envelope = _store.Append("ws1", null, [Entry("removed", "Legacy")]);
        Assert.Equal(2, envelope.Entries.Count);
    }

    [Fact]
    public void Store_path_anchors_under_the_storage_root_when_one_is_set()
    {
        var clean = new SchemaChangeLogStore();
        var root = Path.Combine(Path.GetTempPath(), "bowire-schemachange-root");
        var path = clean.GetStorePath("ws1", root);
        Assert.StartsWith(root, path, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("schema-changes", "log.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_path_sanitises_a_hostile_workspace_id()
    {
        // Pure path computation — no IO. '../../evil ws' must collapse
        // to a harmless slug inside the workspaces folder, mirroring
        // the PresetStore / ChunkedRecordingStore barrier.
        var clean = new SchemaChangeLogStore();
        var path = clean.GetStorePath("../../evil ws", null);
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("workspaces", "evilws"), path, StringComparison.Ordinal);
    }
}

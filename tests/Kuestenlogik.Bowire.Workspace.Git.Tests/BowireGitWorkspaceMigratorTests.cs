// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Workspace.Git;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Behavioural coverage for <see cref="BowireGitWorkspaceMigrator"/> —
/// converts a legacy bundle-shaped workspace (one
/// <c>&lt;entityKind&gt;.json</c> per kind) into the per-entity file
/// layout the git-backed runtime reads through (#196 Phase 2.2).
/// </summary>
public sealed class BowireGitWorkspaceMigratorTests : IDisposable
{
    private readonly string _root;

    public BowireGitWorkspaceMigratorTests()
    {
        _root = Directory.CreateTempSubdirectory("bowire-git-migrate-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----------------------------------------------------------------
    // Bare-array legacy bundle shape
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_converts_bare_array_environments_into_per_entity_files()
    {
        var legacy = Path.Combine(_root, "environments.json");
        await File.WriteAllTextAsync(legacy, """
        [
            { "id": "env_dev",  "name": "Dev"  },
            { "id": "env_prod", "name": "Prod" }
        ]
        """, TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);

        var envKind = report.Kinds.Single(k => k.EntityKind == "environments");
        Assert.True(envKind.LegacyFound);
        Assert.Equal(2, envKind.Migrated);
        Assert.True(report.AnyMigrated);
        Assert.Equal(2, report.TotalEntities);

        // Per-entity files materialised — one JSON document per id.
        var devFile = Path.Combine(_root, "environments", "env_dev.json");
        var prodFile = Path.Combine(_root, "environments", "env_prod.json");
        Assert.True(File.Exists(devFile));
        Assert.True(File.Exists(prodFile));

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(devFile, TestContext.Current.CancellationToken));
        Assert.Equal("Dev", doc.RootElement.GetProperty("name").GetString());

        // Original bundle parked behind .legacy — not deleted, so the
        // operator can verify before binning it.
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".legacy"));
    }

    // ----------------------------------------------------------------
    // Envelope legacy bundle shape (EnvironmentStore-compatible)
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_handles_envelope_shape_with_kind_keyed_array()
    {
        // EnvironmentStore writes {globals, environments[], activeEnvId};
        // the migrator extracts the embedded array.
        var legacy = Path.Combine(_root, "environments.json");
        await File.WriteAllTextAsync(legacy, """
        {
            "globals": { "BASE_URL": "https://api.local" },
            "environments": [
                { "id": "env_a", "name": "A" },
                { "id": "env_b", "name": "B" },
                { "id": "env_c", "name": "C" }
            ],
            "activeEnvId": "env_a"
        }
        """, TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        var kind = report.Kinds.Single(k => k.EntityKind == "environments");
        Assert.Equal(3, kind.Migrated);

        foreach (var id in new[] { "env_a", "env_b", "env_c" })
        {
            Assert.True(File.Exists(Path.Combine(_root, "environments", id + ".json")));
        }
    }

    // ----------------------------------------------------------------
    // Collections — per-request fan-out via the FileEntityStore
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_collections_fans_out_each_request_into_req_json()
    {
        var legacy = Path.Combine(_root, "collections.json");
        await File.WriteAllTextAsync(legacy, """
        [
            {
                "id": "col_payments",
                "name": "Payments",
                "requests": [
                    { "id": "req_list",   "method": "GET" },
                    { "id": "req_create", "method": "POST" }
                ]
            }
        ]
        """, TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Kinds.Single(k => k.EntityKind == "collections").Migrated);

        var dir = Path.Combine(_root, "collections", "col_payments");
        Assert.True(File.Exists(Path.Combine(dir, "col_payments.json")));
        Assert.True(File.Exists(Path.Combine(dir, "req_list.req.json")));
        Assert.True(File.Exists(Path.Combine(dir, "req_create.req.json")));

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(dir, "req_list.req.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal("GET", doc.RootElement.GetProperty("method").GetString());
    }

    // ----------------------------------------------------------------
    // Idempotence
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_is_idempotent_when_no_legacy_bundle_exists()
    {
        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        Assert.False(report.AnyMigrated);
        Assert.Equal(0, report.TotalEntities);
        Assert.All(report.Kinds, k => Assert.False(k.LegacyFound));
    }

    [Fact]
    public async Task MigrateAsync_rerun_after_successful_migrate_is_noop()
    {
        var legacy = Path.Combine(_root, "environments.json");
        await File.WriteAllTextAsync(legacy,
            """[{ "id": "env_only" }]""",
            TestContext.Current.CancellationToken);

        var first = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        Assert.True(first.AnyMigrated);

        // Second run — the .legacy file is in place, the per-entity
        // file is on disk, nothing should move.
        var second = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        Assert.False(second.AnyMigrated);
        Assert.Equal(0, second.TotalEntities);

        // Per-entity file still in place; .legacy still in place.
        Assert.True(File.Exists(Path.Combine(_root, "environments", "env_only.json")));
        Assert.True(File.Exists(legacy + ".legacy"));
    }

    // ----------------------------------------------------------------
    // Partial migration — some kinds present, others missing
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_reports_each_kind_independently()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "environments.json"),
            """[{"id":"env_a"},{"id":"env_b"}]""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "scripts.json"),
            """[{"id":"script_one"}]""",
            TestContext.Current.CancellationToken);
        // recordings/collections/flows: absent.

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Kinds.Single(k => k.EntityKind == "environments").Migrated);
        Assert.Equal(1, report.Kinds.Single(k => k.EntityKind == "scripts").Migrated);
        Assert.False(report.Kinds.Single(k => k.EntityKind == "recordings").LegacyFound);
        Assert.False(report.Kinds.Single(k => k.EntityKind == "collections").LegacyFound);
        Assert.False(report.Kinds.Single(k => k.EntityKind == "flows").LegacyFound);
        Assert.Equal(3, report.TotalEntities);
    }

    // ----------------------------------------------------------------
    // Tolerance — invalid entries are dropped, valid ones survive
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_skips_entries_with_no_id_field_but_keeps_valid_ones()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "environments.json"), """
        [
            { "id": "env_ok",     "name": "Good" },
            { "id": "",           "name": "Empty id" },
            { "name": "No id at all" },
            { "id": 42,           "name": "Non-string id" },
            { "id": "env_other",  "name": "Other" }
        ]
        """, TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Kinds.Single(k => k.EntityKind == "environments").Migrated);
        Assert.True(File.Exists(Path.Combine(_root, "environments", "env_ok.json")));
        Assert.True(File.Exists(Path.Combine(_root, "environments", "env_other.json")));
    }

    [Fact]
    public async Task MigrateAsync_unknown_shape_records_zero_but_still_parks_legacy()
    {
        // A legacy bundle whose root is neither an array nor a
        // kind-keyed envelope. The migrator can't infer the entity
        // shape; it reports zero migrated but still parks the bundle
        // as .legacy so the operator notices.
        var legacy = Path.Combine(_root, "scripts.json");
        await File.WriteAllTextAsync(legacy,
            """{ "shrug": "this isn't a Bowire bundle" }""",
            TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        var scriptsKind = report.Kinds.Single(k => k.EntityKind == "scripts");
        Assert.True(scriptsKind.LegacyFound);
        Assert.Equal(0, scriptsKind.Migrated);

        Assert.True(File.Exists(legacy + ".legacy"));
        Assert.False(File.Exists(legacy));
    }

    // ----------------------------------------------------------------
    // Failure modes
    // ----------------------------------------------------------------

    [Fact]
    public async Task MigrateAsync_throws_when_workspace_root_missing()
    {
        var missing = Path.Combine(_root, "nope");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            BowireGitWorkspaceMigrator.MigrateAsync(missing,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MigrateAsync_throws_on_invalid_json_in_legacy_bundle()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "flows.json"),
            "{ not valid json",
            TestContext.Current.CancellationToken);
        // The concrete failure is JsonReaderException (derives from
        // JsonException); ThrowsAnyAsync covers both.
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            BowireGitWorkspaceMigrator.MigrateAsync(_root,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MigrateAsync_overwrites_existing_dot_legacy_artifact()
    {
        // Operator ran migrate-format, intervened manually, dropped a
        // new <kind>.json — a re-run should re-park without tripping on
        // the existing .legacy file.
        var legacy = Path.Combine(_root, "environments.json");
        var parked = legacy + ".legacy";
        await File.WriteAllTextAsync(parked, "stale",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(legacy,
            """[{"id":"env_fresh"}]""",
            TestContext.Current.CancellationToken);

        var report = await BowireGitWorkspaceMigrator.MigrateAsync(_root,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Kinds.Single(k => k.EntityKind == "environments").Migrated);

        // The previous .legacy was overwritten with the fresh bundle.
        Assert.True(File.Exists(parked));
        var content = await File.ReadAllTextAsync(parked,
            TestContext.Current.CancellationToken);
        Assert.Contains("env_fresh", content);
    }
}

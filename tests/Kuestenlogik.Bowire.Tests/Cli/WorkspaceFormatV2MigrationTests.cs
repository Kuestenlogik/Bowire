// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// #282 — Coverage for the legacy v1 → v2 migration shim that lets
/// readers keep accepting pre-unification .bww shapes through v2.x.
/// Two pre-v2 shapes ship in v2.0:
/// <list type="bullet">
///   <item><b>UI-v1</b> — produced by the workbench's
///     <c>exportWorkspaceJson</c> before #282 A2. Envelope had
///     <c>format/version=1/workspace/data</c>; <c>data</c> fields
///     used raw localStorage bucket names (<c>bowire_*</c>).</item>
///   <item><b>CLI-v1</b> — produced by <c>RunExportAsync</c> before
///     #282 A2. No <c>format</c> header; <c>workspaceFormatVersion=1</c>
///     + top-level per-kind arrays + no <c>workspace</c> identity.</item>
/// </list>
/// Both shapes get rewritten in-memory by
/// <see cref="WorkspaceCommand.MigrateLegacyWorkspaceShape"/>; the
/// rest of the importer sees v2 only. The shim retires in v3.0.0
/// (#283).
/// </summary>
public sealed class WorkspaceFormatV2MigrationTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceFormatV2MigrationTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-format-v2-migration-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----------------------------------------------------------------
    // Pass-through: v2 envelopes are NOT modified by the shim.
    // ----------------------------------------------------------------
    [Fact]
    public void Migrate_v2_payload_is_pass_through()
    {
        var v2 = new JsonObject
        {
            ["format"] = "bowire-workspace",
            ["version"] = WorkspaceCommand.CanonicalFormatVersion,
            ["workspace"] = new JsonObject { ["name"] = "Already v2" },
            ["data"] = new JsonObject
            {
                ["environments"] = new JsonArray(new JsonObject { ["id"] = "env_a" }),
                ["collections"] = new JsonArray()
            }
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(v2);

        Assert.Same(v2, migrated);
        Assert.Null(migrated["_migratedFrom"]);
    }

    // ----------------------------------------------------------------
    // CLI-v1 → v2: per-kind top-level arrays land under data/, an
    // empty workspace identity gets synthesised.
    // ----------------------------------------------------------------
    [Fact]
    public void Migrate_legacy_cli_v1_to_v2_lifts_arrays_under_data()
    {
        var cliV1 = new JsonObject
        {
            ["workspaceFormatVersion"] = 1,
            ["exportedAt"] = "2026-06-23T10:00:00Z",
            ["environments"] = new JsonArray(new JsonObject { ["id"] = "env_a", ["name"] = "A" }),
            ["collections"] = new JsonArray(new JsonObject { ["id"] = "col_a" }),
            ["recordings"] = new JsonArray(),
            ["scripts"] = new JsonArray(),
            ["flows"] = new JsonArray()
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(cliV1);

        Assert.Equal("bowire-workspace", (string?)migrated["format"]);
        Assert.Equal(WorkspaceCommand.CanonicalFormatVersion, (int)migrated["version"]!);
        Assert.Equal("cli-v1", (string?)migrated["_migratedFrom"]);

        var data = migrated["data"] as JsonObject;
        Assert.NotNull(data);
        Assert.Equal(1, (data!["environments"] as JsonArray)!.Count);
        Assert.Equal(1, (data["collections"] as JsonArray)!.Count);
        // Browser-only buckets backfilled with empty defaults.
        Assert.Equal(0, (data["urls"] as JsonArray)!.Count);
        Assert.NotNull(data["urlMeta"] as JsonObject);
        Assert.NotNull(data["globals"] as JsonObject);
        Assert.Null(data["activeEnvironmentId"]);

        // Workspace identity synthesised with placeholder name.
        var ws = migrated["workspace"] as JsonObject;
        Assert.NotNull(ws);
        Assert.Equal("Imported", (string?)ws!["name"]);
        Assert.Equal("#6366f1", (string?)ws["color"]);
    }

    [Fact]
    public void Migrate_legacy_cli_v1_preserves_exportedAt_when_present()
    {
        var cliV1 = new JsonObject
        {
            ["workspaceFormatVersion"] = 1,
            ["exportedAt"] = "2025-12-31T23:59:59Z",
            ["environments"] = new JsonArray()
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(cliV1);

        Assert.Equal("2025-12-31T23:59:59Z", (string?)migrated["exportedAt"]);
    }

    [Fact]
    public void Migrate_legacy_cli_v1_synthesises_exportedAt_when_missing()
    {
        var cliV1 = new JsonObject
        {
            ["workspaceFormatVersion"] = 1,
            ["environments"] = new JsonArray()
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(cliV1);

        Assert.False(string.IsNullOrEmpty((string?)migrated["exportedAt"]));
    }

    // ----------------------------------------------------------------
    // UI-v1 → v2: data field names translate (raw localStorage keys
    // become canonical short names) + pluginPins lifts onto workspace
    // identity.
    // ----------------------------------------------------------------
    [Fact]
    public void Migrate_legacy_ui_v1_to_v2_keeps_format_header_bumps_version()
    {
        var uiV1 = new JsonObject
        {
            ["format"] = "bowire-workspace",
            ["version"] = 1,
            ["workspace"] = new JsonObject { ["name"] = "UI Workspace", ["color"] = "#abcdef" },
            ["data"] = new JsonObject
            {
                ["bowire_server_urls"] = new JsonArray("https://api.example.com"),
                ["bowire_environments"] = new JsonArray(new JsonObject { ["id"] = "env_dev" }),
                ["bowire_plugin_pins"] = new JsonObject { ["rest"] = ">=2.0" }
            }
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(uiV1);

        Assert.Equal("bowire-workspace", (string?)migrated["format"]);
        Assert.Equal(WorkspaceCommand.CanonicalFormatVersion, (int)migrated["version"]!);
        Assert.Equal("ui-v1", (string?)migrated["_migratedFrom"]);

        var ws = migrated["workspace"] as JsonObject;
        Assert.NotNull(ws);
        Assert.Equal("UI Workspace", (string?)ws!["name"]);
        Assert.Equal("#abcdef", (string?)ws["color"]);
    }

    // ----------------------------------------------------------------
    // Unrecognised shapes: pass-through (the downstream validator
    // throws the canonical error).
    // ----------------------------------------------------------------
    [Fact]
    public void Migrate_unrecognised_shape_is_passed_through_unchanged()
    {
        // Plain object with no workspace markers — neither format
        // header nor workspaceFormatVersion field. Migration shouldn't
        // touch it; the downstream version check rejects it.
        var random = new JsonObject
        {
            ["totallyUnrelated"] = "data"
        };

        var migrated = WorkspaceCommand.MigrateLegacyWorkspaceShape(random);

        Assert.Same(random, migrated);
    }

    // ----------------------------------------------------------------
    // End-to-end: legacy CLI-v1 fixture imports cleanly via the v2
    // pipeline. Pins the migration's WIRE behaviour, not just the
    // in-memory rewrite.
    // ----------------------------------------------------------------
    [Fact]
    public async Task RunImportAsync_legacy_cli_v1_fixture_materialises_v2_entities()
    {
        var fixture = SafePath.Combine(_tempRoot, "legacy-cli.json");
        // Hand-rolled v1-CLI fixture — what RunExportAsync produced
        // before #282 A2.
        await File.WriteAllTextAsync(fixture,
            "{\"workspaceFormatVersion\":1,\"exportedAt\":\"2026-01-01T00:00:00Z\","
            + "\"environments\":[{\"id\":\"env_legacy\",\"name\":\"Legacy\"}],"
            + "\"collections\":[],\"recordings\":[],\"scripts\":[],\"flows\":[]}",
            TestContext.Current.CancellationToken);

        var target = SafePath.Combine(_tempRoot, "out-cli");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(fixture, target,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(SafePath.Combine(target, "environments", "env_legacy.json")));
    }

    [Fact]
    public async Task RunImportAsync_legacy_cli_v1_round_trips_through_v2_writer()
    {
        // Land a v1-CLI fixture, import it (shim migrates → v2 → writes
        // per-entity files), then re-export the resulting directory.
        // The fresh export MUST land in v2 canonical envelope shape.
        var fixture = SafePath.Combine(_tempRoot, "rt-input.json");
        await File.WriteAllTextAsync(fixture,
            "{\"workspaceFormatVersion\":1,\"environments\":[{\"id\":\"env_rt\",\"name\":\"RT\"}]}",
            TestContext.Current.CancellationToken);
        var middle = SafePath.Combine(_tempRoot, "rt-middle");

        using var stdoutI = new StringWriter();
        using var stderrI = new StringWriter();
        var rcImport = await WorkspaceCommand.RunImportAsync(fixture, middle,
            stdoutI, stderrI, TestContext.Current.CancellationToken);
        Assert.Equal(0, rcImport);

        var fresh = SafePath.Combine(_tempRoot, "rt-output.bww");
        using var stdoutE = new StringWriter();
        using var stderrE = new StringWriter();
        var rcExport = await WorkspaceCommand.RunExportAsync(middle, fresh,
            stdoutE, stderrE, TestContext.Current.CancellationToken);
        Assert.Equal(0, rcExport);

        var freshRaw = await File.ReadAllTextAsync(fresh, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(freshRaw);
        Assert.Equal("bowire-workspace",
            doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(WorkspaceCommand.CanonicalFormatVersion,
            doc.RootElement.GetProperty("version").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("workspace", out _));
        Assert.True(doc.RootElement.TryGetProperty("data", out var dataEl));
        Assert.True(dataEl.TryGetProperty("environments", out var envs));
        Assert.Equal(1, envs.GetArrayLength());
        Assert.Equal("env_rt", envs[0].GetProperty("id").GetString());
    }

    // ----------------------------------------------------------------
    // Small filesystem helper — mirrors the pattern used by sibling
    // tests for consistency.
    // ----------------------------------------------------------------
    private static class SafePath
    {
        public static string Combine(params string[] parts) => Path.Combine(parts);
    }
}

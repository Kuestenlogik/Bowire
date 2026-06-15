// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for <c>bowire workspace migrate-format</c>
/// (#196 Phase 2.2). Mirrors the output-capture pattern landed in
/// <c>WorkspaceCommandEdgeCasesTests</c>: per-test StringWriter pair,
/// concrete substring assertions on stdout/stderr + per-entity counts,
/// no "doesn't throw" tests.
/// </summary>
public sealed class WorkspaceMigrateFormatTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceMigrateFormatTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-workspace-migrate-tests-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----------------------------------------------------------------
    // Build() factory + System.CommandLine wiring
    // ----------------------------------------------------------------

    [Fact]
    public void Build_workspace_command_advertises_migrate_format_subcommand()
    {
        var workspace = WorkspaceCommand.Build();
        var migrate = workspace.Subcommands.SingleOrDefault(s => s.Name == "migrate-format");
        Assert.NotNull(migrate);

        // Locks down the description copy — surfaces "per-entity" so
        // operators searching `bowire workspace --help` find the
        // command by intent, not by name.
        Assert.Contains("per-entity", migrate!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Idempotent", migrate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(migrate.Arguments, a => a.Name == "path");
    }

    [Fact]
    public async Task Build_parse_then_invoke_runs_migrate_format_end_to_end()
    {
        // Drives the SetAction callback registered in
        // BuildMigrateFormatCommand through System.CommandLine.
        var ws = Path.Combine(_tempRoot, "via-parse");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "environments.json"),
            """[{"id":"env_x","name":"X"}]""",
            TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var workspace = WorkspaceCommand.Build();
        var parse = workspace.Parse(new[] { "migrate-format", ws });
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());
        var output = stdout.ToString();
        Assert.Contains("Migrated workspace at", output);
        Assert.Contains("environments: 1 entity", output);
        Assert.True(File.Exists(Path.Combine(ws, "environments", "env_x.json")));
    }

    // ----------------------------------------------------------------
    // Direct RunMigrateFormatAsync coverage
    // ----------------------------------------------------------------

    [Fact]
    public async Task RunMigrateFormatAsync_rejects_empty_path_with_usage_exit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync("",
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("path argument", stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task RunMigrateFormatAsync_rejects_whitespace_path_with_usage_exit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync("   ",
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("path argument", stderr.ToString());
    }

    [Fact]
    public async Task RunMigrateFormatAsync_returns_no_input_when_directory_missing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var missing = Path.Combine(_tempRoot, "never-existed");
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(missing,
            stdout, stderr, TestContext.Current.CancellationToken);

        // EX_NOINPUT — directory not found.
        Assert.Equal(66, rc);
        Assert.Contains("does not exist", stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task RunMigrateFormatAsync_reports_per_entity_counts_on_success()
    {
        var ws = Path.Combine(_tempRoot, "full-migrate");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "environments.json"),
            """[{"id":"env_a"},{"id":"env_b"},{"id":"env_c"}]""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ws, "scripts.json"),
            """[{"id":"script_one"},{"id":"script_two"}]""",
            TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());

        var output = stdout.ToString();
        Assert.Contains("Migrated workspace at", output);
        Assert.Contains("environments: 3 entity", output);
        Assert.Contains("scripts: 2 entity", output);
        Assert.Contains("5 entity(ies) migrated total", output);
        // Skipped kinds (recordings / collections / flows) get a
        // distinct diagnostic so operators see them at a glance.
        Assert.Contains("recordings: skipped", output);
        Assert.Contains("collections: skipped", output);
        Assert.Contains("flows: skipped", output);
        // .legacy hint must surface so operators clean up after
        // verifying.
        Assert.Contains(".legacy", output);

        // Per-entity files materialised, original bundles parked.
        Assert.True(File.Exists(Path.Combine(ws, "environments", "env_a.json")));
        Assert.True(File.Exists(Path.Combine(ws, "scripts", "script_two.json")));
        Assert.True(File.Exists(Path.Combine(ws, "environments.json.legacy")));
        Assert.True(File.Exists(Path.Combine(ws, "scripts.json.legacy")));
    }

    [Fact]
    public async Task RunMigrateFormatAsync_is_idempotent_on_already_migrated_workspace()
    {
        var ws = Path.Combine(_tempRoot, "idempotent");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "environments.json"),
            """[{"id":"env_only"}]""",
            TestContext.Current.CancellationToken);

        using var stdout1 = new StringWriter();
        using var stderr1 = new StringWriter();
        var first = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout1, stderr1, TestContext.Current.CancellationToken);
        Assert.Equal(0, first);
        Assert.Contains("Migrated workspace", stdout1.ToString());

        using var stdout2 = new StringWriter();
        using var stderr2 = new StringWriter();
        var second = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout2, stderr2, TestContext.Current.CancellationToken);
        Assert.Equal(0, second);
        Assert.Empty(stderr2.ToString());
        var output2 = stdout2.ToString();
        Assert.Contains("nothing to do", output2);
        Assert.Contains("already per-entity layout", output2);
        // The "Migrated workspace" header must NOT appear on the no-op
        // run — otherwise operators can't tell the two paths apart.
        Assert.DoesNotContain("Migrated workspace at", output2);
    }

    [Fact]
    public async Task RunMigrateFormatAsync_reports_invalid_json_with_ex_dataerr()
    {
        var ws = Path.Combine(_tempRoot, "bad-json");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "environments.json"),
            "{ not json",
            TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(65, rc);
        var err = stderr.ToString();
        Assert.Contains("workspace migrate-format:", err);
        Assert.Contains("not valid JSON", err);
    }

    [Fact]
    public async Task RunMigrateFormatAsync_preserves_legacy_bundle_under_dot_legacy_extension()
    {
        var ws = Path.Combine(_tempRoot, "preserve");
        Directory.CreateDirectory(ws);
        var legacy = Path.Combine(ws, "environments.json");
        var originalContent = """[{"id":"env_preserve","name":"Preserve"}]""";
        await File.WriteAllTextAsync(legacy, originalContent,
            TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);

        // Original gone; .legacy alongside the per-entity layout.
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".legacy"));
        var legacyContent = await File.ReadAllTextAsync(legacy + ".legacy",
            TestContext.Current.CancellationToken);
        Assert.Equal(originalContent, legacyContent);
    }

    [Fact]
    public async Task RunMigrateFormatAsync_collections_creates_per_request_files()
    {
        var ws = Path.Combine(_tempRoot, "collections-fanout");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "collections.json"), """
        [
            {
                "id": "col_main",
                "name": "Main",
                "requests": [
                    { "id": "req_alpha", "method": "GET" },
                    { "id": "req_beta",  "method": "POST" }
                ]
            }
        ]
        """, TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);

        // Container + two .req.json siblings.
        var colDir = Path.Combine(ws, "collections", "col_main");
        Assert.True(File.Exists(Path.Combine(colDir, "col_main.json")));
        Assert.True(File.Exists(Path.Combine(colDir, "req_alpha.req.json")));
        Assert.True(File.Exists(Path.Combine(colDir, "req_beta.req.json")));

        // Container metadata round-trips intact through the migration
        // → re-serialisation path.
        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(colDir, "col_main.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal("Main", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RunMigrateFormatAsync_handles_envelope_shape_environment_bundle()
    {
        // EnvironmentStore's on-disk envelope ({globals, environments,
        // activeEnvId}) is the most common legacy shape — the CLI must
        // surface its count in the per-kind line.
        var ws = Path.Combine(_tempRoot, "envelope");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "environments.json"), """
        {
            "globals": { "BASE": "https://api.local" },
            "environments": [
                { "id": "env_one" },
                { "id": "env_two" }
            ],
            "activeEnvId": "env_one"
        }
        """, TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunMigrateFormatAsync(ws,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Contains("environments: 2 entity", stdout.ToString());
        Assert.True(File.Exists(Path.Combine(ws, "environments", "env_one.json")));
        Assert.True(File.Exists(Path.Combine(ws, "environments", "env_two.json")));
    }
}

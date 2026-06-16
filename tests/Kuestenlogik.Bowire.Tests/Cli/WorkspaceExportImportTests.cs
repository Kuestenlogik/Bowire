// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for <c>bowire workspace export</c> +
/// <c>workspace import</c> (#149 closeout). Mirrors the
/// <see cref="WorkspaceMigrateFormatTests"/> pattern: per-test
/// StringWriter pair, concrete substring assertions on stdout/stderr,
/// sysexits-style exit-code checks, and a full export→import
/// round-trip pinning the per-entity file layout.
/// </summary>
public sealed class WorkspaceExportImportTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceExportImportTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-workspace-export-tests-").FullName;
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
    public void Build_workspace_command_advertises_export_and_import_subcommands()
    {
        var workspace = WorkspaceCommand.Build();
        var export = workspace.Subcommands.SingleOrDefault(s => s.Name == "export");
        var import = workspace.Subcommands.SingleOrDefault(s => s.Name == "import");

        Assert.NotNull(export);
        Assert.NotNull(import);
        Assert.Contains(export!.Arguments, a => a.Name == "path");
        Assert.Contains(import!.Arguments, a => a.Name == "path");
        Assert.Contains(export.Options, o => o.Name == "--from");
        Assert.Contains(import.Options, o => o.Name == "--to");
    }

    [Fact]
    public async Task Build_parse_then_invoke_runs_export_end_to_end()
    {
        var ws = SafePath.Combine(_tempRoot, "via-parse-source");
        Directory.CreateDirectory(SafePath.Combine(ws, "environments"));
        await File.WriteAllTextAsync(SafePath.Combine(ws, "environments", "env_a.json"),
            """{"id":"env_a","name":"A"}""",
            TestContext.Current.CancellationToken);
        var outFile = SafePath.Combine(_tempRoot, "via-parse.json");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var workspace = WorkspaceCommand.Build();
        var parse = workspace.Parse(new[] { "export", "--from", ws, outFile });
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());
        Assert.True(File.Exists(outFile));
        var output = stdout.ToString();
        Assert.Contains("Exported workspace at", output);
        Assert.Contains("environments: 1", output);
    }

    // ----------------------------------------------------------------
    // RunExportAsync — direct path
    // ----------------------------------------------------------------

    [Fact]
    public async Task RunExportAsync_rejects_empty_output_path_with_usage_exit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await WorkspaceCommand.RunExportAsync(_tempRoot, "",
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("output path", stderr.ToString());
    }

    [Fact]
    public async Task RunExportAsync_returns_no_input_when_source_missing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var missing = SafePath.Combine(_tempRoot, "never-existed");
        var outFile = SafePath.Combine(_tempRoot, "out.json");

        var rc = await WorkspaceCommand.RunExportAsync(missing, outFile,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(66, rc);
        Assert.Contains("does not exist", stderr.ToString());
    }

    [Fact]
    public async Task RunExportAsync_empty_workspace_writes_envelope_with_zero_counts()
    {
        var ws = SafePath.Combine(_tempRoot, "empty-ws");
        Directory.CreateDirectory(ws);
        var outFile = SafePath.Combine(_tempRoot, "empty.json");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunExportAsync(ws, outFile,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(outFile));
        var json = await File.ReadAllTextAsync(outFile, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(WorkspaceCommand.ExportFormatVersion,
            doc.RootElement.GetProperty("workspaceFormatVersion").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("environments", out var env));
        Assert.Equal(0, env.GetArrayLength());

        var output = stdout.ToString();
        Assert.Contains("0 entity(ies) exported total", output);
        // Every kind appears with a "0" bullet — pins the per-kind line
        // shape so the summary stays diff-able in CI logs.
        Assert.Contains("· environments: 0", output);
        Assert.Contains("· collections: 0", output);
        Assert.Contains("· recordings: 0", output);
        Assert.Contains("· scripts: 0", output);
        Assert.Contains("· flows: 0", output);
    }

    [Fact]
    public async Task RunExportAsync_populated_workspace_reports_per_kind_counts()
    {
        var ws = SafePath.Combine(_tempRoot, "populated");
        Directory.CreateDirectory(SafePath.Combine(ws, "environments"));
        Directory.CreateDirectory(SafePath.Combine(ws, "scripts"));
        await File.WriteAllTextAsync(SafePath.Combine(ws, "environments", "env_a.json"),
            """{"id":"env_a","name":"A"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(SafePath.Combine(ws, "environments", "env_b.json"),
            """{"id":"env_b","name":"B"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(SafePath.Combine(ws, "scripts", "script_one.json"),
            """{"id":"script_one","body":"console.log(1)"}""",
            TestContext.Current.CancellationToken);
        var outFile = SafePath.Combine(_tempRoot, "populated.json");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunExportAsync(ws, outFile,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = stdout.ToString();
        Assert.Contains("→ environments: 2", output);
        Assert.Contains("→ scripts: 1", output);
        Assert.Contains("3 entity(ies) exported total", output);
    }

    // ----------------------------------------------------------------
    // RunImportAsync — direct path
    // ----------------------------------------------------------------

    [Fact]
    public async Task RunImportAsync_rejects_empty_input_path_with_usage_exit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await WorkspaceCommand.RunImportAsync("", _tempRoot,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("input path", stderr.ToString());
    }

    [Fact]
    public async Task RunImportAsync_returns_no_input_when_file_missing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var missing = SafePath.Combine(_tempRoot, "no-such.json");

        var rc = await WorkspaceCommand.RunImportAsync(missing, _tempRoot,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(66, rc);
        Assert.Contains("does not exist", stderr.ToString());
    }

    [Fact]
    public async Task RunImportAsync_rejects_malformed_json_with_data_err_exit()
    {
        var file = SafePath.Combine(_tempRoot, "bad.json");
        await File.WriteAllTextAsync(file, "{not json", TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(file, _tempRoot,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(65, rc);
        Assert.Contains("not a valid workspace export", stderr.ToString());
    }

    [Fact]
    public async Task RunImportAsync_rejects_non_object_root_with_data_err_exit()
    {
        var file = SafePath.Combine(_tempRoot, "array.json");
        await File.WriteAllTextAsync(file, "[1, 2, 3]", TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(file, _tempRoot,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(65, rc);
    }

    [Fact]
    public async Task RunImportAsync_refuses_future_format_version()
    {
        var file = SafePath.Combine(_tempRoot, "future.json");
        var futureVersion = WorkspaceCommand.ExportFormatVersion + 1;
        await File.WriteAllTextAsync(file,
            $$"""{"workspaceFormatVersion":{{futureVersion}},"environments":[]}""",
            TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(file, _tempRoot,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(65, rc);
        var err = stderr.ToString();
        Assert.Contains("Update Bowire", err);
        Assert.Contains($"format version {futureVersion}", err);
    }

    [Fact]
    public async Task RunImportAsync_materialises_entities_into_target_dir()
    {
        var file = SafePath.Combine(_tempRoot, "fresh.json");
        await File.WriteAllTextAsync(file,
            $$"""
            {
                "workspaceFormatVersion": {{WorkspaceCommand.ExportFormatVersion}},
                "environments": [{"id":"env_a","name":"A"},{"id":"env_b","name":"B"}],
                "scripts": [{"id":"script_one","body":"console.log(1)"}],
                "collections": [],
                "recordings": [],
                "flows": []
            }
            """,
            TestContext.Current.CancellationToken);
        var target = SafePath.Combine(_tempRoot, "import-into");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(file, target,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());
        Assert.True(File.Exists(SafePath.Combine(target, "environments", "env_a.json")));
        Assert.True(File.Exists(SafePath.Combine(target, "environments", "env_b.json")));
        Assert.True(File.Exists(SafePath.Combine(target, "scripts", "script_one.json")));

        var output = stdout.ToString();
        Assert.Contains("→ environments: 2", output);
        Assert.Contains("→ scripts: 1", output);
        Assert.Contains("3 entity(ies) imported total", output);
    }

    [Fact]
    public async Task RunImportAsync_skips_entries_without_id()
    {
        // Entries without a stable id can't be materialised — the
        // per-entity file layout keys on id. Pin the silent-skip so
        // a partial export doesn't take the whole import down.
        var file = SafePath.Combine(_tempRoot, "id-mix.json");
        await File.WriteAllTextAsync(file,
            $$"""
            {
                "workspaceFormatVersion": {{WorkspaceCommand.ExportFormatVersion}},
                "environments": [{"id":"env_keep","name":"K"},{"name":"no-id"},{"id":"","name":"empty-id"}]
            }
            """,
            TestContext.Current.CancellationToken);
        var target = SafePath.Combine(_tempRoot, "id-mix-into");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunImportAsync(file, target,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(SafePath.Combine(target, "environments", "env_keep.json")));
        Assert.Contains("→ environments: 1", stdout.ToString());
    }

    // ----------------------------------------------------------------
    // Round-trip — export → import preserves entity set
    // ----------------------------------------------------------------

    [Fact]
    public async Task Export_then_import_round_trips_per_entity_files()
    {
        // Source workspace with a mixed entity set, then export →
        // import into a fresh directory, and assert the new directory
        // has exactly the same per-entity files. Pins #149's
        // "round-trip without touching ~/.bowire/" promise.
        var source = SafePath.Combine(_tempRoot, "rt-source");
        Directory.CreateDirectory(SafePath.Combine(source, "environments"));
        Directory.CreateDirectory(SafePath.Combine(source, "collections"));
        await File.WriteAllTextAsync(SafePath.Combine(source, "environments", "env_x.json"),
            """{"id":"env_x","name":"X","variables":{"K":"v"}}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(SafePath.Combine(source, "collections", "col_main.json"),
            """{"id":"col_main","title":"Main","items":[]}""",
            TestContext.Current.CancellationToken);

        var exportFile = SafePath.Combine(_tempRoot, "rt-export.json");
        var target = SafePath.Combine(_tempRoot, "rt-target");

        using var sw1 = new StringWriter();
        using var se1 = new StringWriter();
        Assert.Equal(0, await WorkspaceCommand.RunExportAsync(source, exportFile,
            sw1, se1, TestContext.Current.CancellationToken));

        using var sw2 = new StringWriter();
        using var se2 = new StringWriter();
        Assert.Equal(0, await WorkspaceCommand.RunImportAsync(exportFile, target,
            sw2, se2, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(SafePath.Combine(target, "environments", "env_x.json")));
        // Collections use a per-id subdirectory layout (<id>/<id>.json
        // plus optional .req.json siblings) — verify the container, not
        // a top-level file.
        Assert.True(File.Exists(SafePath.Combine(SafePath.Combine(target, "collections", "col_main"), "col_main.json")));

        // Value preservation: parse the imported file and confirm the
        // variables map survived through JsonNode → SaveAsync.
        var imported = await File.ReadAllTextAsync(
            SafePath.Combine(target, "environments", "env_x.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(imported);
        Assert.Equal("X", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("v", doc.RootElement.GetProperty("variables").GetProperty("K").GetString());
    }
}

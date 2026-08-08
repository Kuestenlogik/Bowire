// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Unit coverage for <c>bowire workspace migrate --to-project</c> (#172) via the
/// path-based core <see cref="WorkspaceCommand.MigrateWorkspaceToProjectAsync"/>.
/// Builds a temp workspace tree (collections / recordings / auth-recordings /
/// workspace.json) and asserts the emitted <c>.bowire/project.json</c> captures
/// the fields that map cleanly and round-trips through the project loader. No
/// <c>~/.bowire</c> or cwd mutation, so the class stays parallel-safe.
/// </summary>
public sealed class WorkspaceMigrateToProjectTests : IDisposable
{
    private const int Ok = 0;
    private const int CantCreat = 73;
    private const int NoInput = 66;

    private readonly string _tempRoot;

    public WorkspaceMigrateToProjectTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-ws-migrate-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private string SeedWorkspace(string id)
    {
        var wsRoot = Path.Combine(_tempRoot, "ws", id);
        Directory.CreateDirectory(Path.Combine(wsRoot, "collections"));
        Directory.CreateDirectory(Path.Combine(wsRoot, "recordings", "rec-1"));
        Directory.CreateDirectory(Path.Combine(wsRoot, "auth-recordings"));

        File.WriteAllText(Path.Combine(wsRoot, "workspace.json"),
            """{ "name": "Payments API", "workspaceFormatVersion": 1 }""");

        // A single-file collection → suite id "smoke".
        File.WriteAllText(Path.Combine(wsRoot, "collections", "smoke.json"),
            """{ "id": "smoke", "name": "Smoke", "requests": [] }""");

        // A recording with steps against two hosts (one seen twice, distinct
        // paths) → two distinct base URLs.
        File.WriteAllText(Path.Combine(wsRoot, "recordings", "rec-1", "recording.json"),
            """
            {
              "id": "rec-1",
              "steps": [
                { "id": "s1", "serverUrl": "https://api.example.com/v1/pets" },
                { "id": "s2", "serverUrl": "https://api.example.com/v1/orders" },
                { "id": "s3", "serverUrl": "http://localhost:5181/pets" }
              ]
            }
            """);

        // A captured auth recording → security.auth ref.
        File.WriteAllText(Path.Combine(wsRoot, "auth-recordings", "login.json"),
            """{ "id": "login", "scheme": "bearer", "credential": "x" }""");

        return wsRoot;
    }

    [Fact]
    public async Task Migrate_maps_sources_suites_and_auth_and_round_trips()
    {
        var wsRoot = SeedWorkspace("payments");
        var outDir = Path.Combine(_tempRoot, "repo");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
            wsRoot, "payments", outDir, force: false, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(Ok, rc);
        Assert.Empty(stderr.ToString());

        var manifestPath = Path.Combine(outDir, ".bowire", "project.json");
        Assert.True(File.Exists(manifestPath));

        var project = BowireProjectLoader.Load(manifestPath);
        Assert.Empty(project.Validate());
        Assert.Equal(BowireProjectFile.SchemaUrl, project.Schema);
        Assert.Equal(1, project.Version);
        Assert.Equal("Payments API", project.Name);

        // Two distinct base URLs, path-stripped + de-duplicated.
        Assert.Equal(2, project.Sources.Count);
        Assert.Contains(project.Sources, s => s.Url == "https://api.example.com");
        Assert.Contains(project.Sources, s => s.Url == "http://localhost:5181");

        Assert.True(project.Suites.ContainsKey("smoke"));
        Assert.Equal("./bowire/suites/smoke.collection.json", project.Suites["smoke"]);

        Assert.NotNull(project.Security);
        Assert.Equal("./bowire/auth/login.flow.json", project.Security!.Auth);
    }

    [Fact]
    public async Task Migrate_refuses_to_overwrite_without_force_then_succeeds_with_force()
    {
        var wsRoot = SeedWorkspace("payments");
        var outDir = Path.Combine(_tempRoot, "repo");

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
                wsRoot, "payments", outDir, force: false, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(Ok, rc);
        }

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
                wsRoot, "payments", outDir, force: false, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(CantCreat, rc);
            Assert.Contains("--force", stderr.ToString(), StringComparison.Ordinal);
        }

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
                wsRoot, "payments", outDir, force: true, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(Ok, rc);
            Assert.Empty(stderr.ToString());
        }
    }

    [Fact]
    public async Task Migrate_missing_workspace_directory_exits_no_input()
    {
        var wsRoot = Path.Combine(_tempRoot, "does-not-exist");
        var outDir = Path.Combine(_tempRoot, "repo");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
            wsRoot, "ghost", outDir, force: false, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(NoInput, rc);
        Assert.NotEmpty(stderr.ToString());
    }

    [Fact]
    public async Task Migrate_empty_workspace_still_writes_a_valid_versioned_manifest()
    {
        var wsRoot = Path.Combine(_tempRoot, "ws", "empty");
        Directory.CreateDirectory(wsRoot);
        var outDir = Path.Combine(_tempRoot, "repo-empty");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.MigrateWorkspaceToProjectAsync(
            wsRoot, "empty", outDir, force: false, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(Ok, rc);
        var project = BowireProjectLoader.Load(Path.Combine(outDir, ".bowire", "project.json"));
        Assert.Empty(project.Validate());
        Assert.Empty(project.Sources);
        Assert.Empty(project.Suites);
        Assert.Null(project.Security);
        Assert.Equal("empty", project.Name);
    }
}

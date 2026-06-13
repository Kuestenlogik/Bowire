// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Unit coverage for <see cref="WorkspaceCommand.RunInitAsync"/> — the
/// `bowire workspace init &lt;path&gt;` command. Verifies the folder
/// skeleton, the workspace.json manifest, the .gitignore template, and
/// the empty-directory guard. The git-init side-effect is best-effort
/// (skips silently when git isn't on PATH) so the tests pass
/// <c>--no-git</c> to keep them hermetic.
/// </summary>
public sealed class WorkspaceCommandTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            "bowire-ws-init-" + Guid.NewGuid().ToString("N")[..10]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task RunInitAsync_creates_folder_skeleton_with_gitkeep_per_subdir()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var path = Path.Combine(_tempRoot, "fresh");
        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());

        foreach (var sub in new[] { "environments", "collections", "recordings", "scripts", "flows", "secrets" })
        {
            var subPath = Path.Combine(path, sub);
            Assert.True(Directory.Exists(subPath), $"subdir missing: {sub}");
            Assert.True(File.Exists(Path.Combine(subPath, ".gitkeep")), $".gitkeep missing in {sub}");
        }
    }

    [Fact]
    public async Task RunInitAsync_writes_manifest_with_schema_version_and_directory_basename()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = Path.Combine(_tempRoot, "payments-team");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var manifestPath = Path.Combine(path, "workspace.json");
        Assert.True(File.Exists(manifestPath));

        var json = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("workspaceFormatVersion").GetInt32());
        Assert.Equal("payments-team", root.GetProperty("name").GetString());
        Assert.Equal("#6366f1", root.GetProperty("color").GetString());
        Assert.StartsWith("ws_", root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("includeAllEnvironments").GetBoolean());
        Assert.True(root.TryGetProperty("pluginPins", out _));
    }

    [Fact]
    public async Task RunInitAsync_uses_name_and_color_flags_when_provided()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = Path.Combine(_tempRoot, "with-flags");

        var rc = await WorkspaceCommand.RunInitAsync(path,
            displayName: "Payments — staging", color: "#22c55e",
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var json = await File.ReadAllTextAsync(Path.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Payments — staging", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("#22c55e", doc.RootElement.GetProperty("color").GetString());
    }

    [Fact]
    public async Task RunInitAsync_drops_gitignore_with_secret_and_cache_excludes()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = Path.Combine(_tempRoot, "gitignore");
        await WorkspaceCommand.RunInitAsync(path, null, null, noGit: true,
            stdout, stderr, TestContext.Current.CancellationToken);

        var gitignore = await File.ReadAllTextAsync(Path.Combine(path, ".gitignore"),
            TestContext.Current.CancellationToken);

        Assert.Contains("environments/*.secrets.json", gitignore);
        Assert.Contains("secrets/*", gitignore);
        Assert.Contains("!secrets/.gitkeep", gitignore);
        Assert.Contains("recordings/bodies/", gitignore);
        Assert.Contains(".bowire-cache/", gitignore);
        Assert.Contains("*.legacy", gitignore);
    }

    [Fact]
    public async Task RunInitAsync_refuses_to_clobber_non_empty_directory()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = Path.Combine(_tempRoot, "non-empty");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "existing.txt"), "do not touch",
            TestContext.Current.CancellationToken);

        var rc = await WorkspaceCommand.RunInitAsync(path, null, null, noGit: true,
            stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(65, rc);
        Assert.Contains("not empty", stderr.ToString());
        // The pre-existing file is untouched.
        Assert.Equal("do not touch",
            await File.ReadAllTextAsync(Path.Combine(path, "existing.txt"), TestContext.Current.CancellationToken));
        // No workspace.json was written.
        Assert.False(File.Exists(Path.Combine(path, "workspace.json")));
    }

    [Fact]
    public async Task RunInitAsync_rejects_empty_path_argument()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await WorkspaceCommand.RunInitAsync(path: "", displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("path argument", stderr.ToString());
    }
}

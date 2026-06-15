// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire workspace</c> — git-backed workspace tooling (#147 / #148 / #149 / #151).
///
/// <para>
/// Materialises the per-entity directory layout the workbench's
/// git-native workspace mode reads from / writes to. Issue #149's
/// <c>init</c> sets up a fresh workspace directory with the
/// canonical folder shape, a default <c>.gitignore</c> that excludes
/// secrets + cache files, a <c>workspace.json</c> manifest with the
/// current schema version, and optionally runs <c>git init</c>.
/// </para>
///
/// <para>
/// <c>export</c> / <c>import</c> / <c>migrate-format</c> follow in
/// Phase 2 once the workbench reads from a workspace's <c>storageRoot</c>
/// directly (today the workbench still routes through the
/// <c>~/.bowire/</c> per-user folder; the migration is a separate
/// piece of work tracked under the cluster's Phase-2 follow-up).
/// </para>
/// </summary>
internal static class WorkspaceCommand
{
    private static readonly JsonSerializerOptions IndentedJsonOpts =
        new() { WriteIndented = true };

    private static readonly string[] WorkspaceSubdirs =
        ["environments", "collections", "recordings", "scripts", "flows", "secrets"];

    private static readonly string[] GitignoreLines =
    [
        "# Bowire workspace .gitignore — Phase 1 (#151 secret separation)",
        "# Per-env secret overlays. The non-secret <env>.json files",
        "# stay committed; their .secrets.json siblings carry the",
        "# tokens that don't belong in git.",
        "environments/*.secrets.json",
        "",
        "# Workspace-wide secret files (cross-env). One file per",
        "# named secret; bodies never enter version control.",
        "secrets/*",
        "!secrets/.gitkeep",
        "",
        "# Recording bodies — large binary payloads cached on disk",
        "# but never reviewed in PRs. The recording.json manifest",
        "# stays committed so the team sees what was captured.",
        "recordings/bodies/",
        "",
        "# Workbench cache (bundle-format conversions, watcher state).",
        ".bowire-cache/",
        "",
        "# Legacy bundle files left behind by `workspace migrate-format`",
        "# (Phase 2). Keep the legacy bundle out of the repo while",
        "# operators trickle through the migration.",
        "*.legacy",
        "",
    ];

    public static Command Build()
    {
        var workspace = new Command("workspace",
            "Manage Bowire workspaces — init a git-backed workspace directory (#147 / #149).");
        workspace.Add(BuildInitCommand());
        return workspace;
    }

    private static Command BuildInitCommand()
    {
        var init = new Command("init",
            "Materialise a fresh git-backed workspace directory at the given path. Drops the canonical folder skeleton (environments / collections / recordings / scripts / flows / secrets), a workspace.json manifest, and a default .gitignore that excludes secrets + cache files. Runs `git init` unless --no-git is passed.");

        var pathArg = new Argument<string>("path")
        {
            Description = "Directory to initialise. Created if it doesn't exist; required to be empty so the init never clobbers existing content."
        };
        var nameOpt = new Option<string?>("--name")
        {
            Description = "Workspace display name written into workspace.json. Defaults to the directory's basename."
        };
        var colorOpt = new Option<string?>("--color")
        {
            Description = "Workspace accent color (hex like '#22c55e'). Defaults to '#6366f1'."
        };
        var noGitOpt = new Option<bool>("--no-git")
        {
            Description = "Skip the trailing `git init`. Useful when initialising inside an existing repository."
        };

        init.Add(pathArg);
        init.Add(nameOpt);
        init.Add(colorOpt);
        init.Add(noGitOpt);

        init.SetAction((pr, ct) =>
        {
            var path = pr.GetValue(pathArg)!;
            var name = pr.GetValue(nameOpt);
            var color = pr.GetValue(colorOpt);
            var noGit = pr.GetValue(noGitOpt);
            return RunInitAsync(path, name, color, noGit,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct);
        });

        return init;
    }

    // Internal so the unit tests can exercise the materialisation logic
    // without spinning up System.CommandLine.
    internal static async Task<int> RunInitAsync(
        string path,
        string? displayName,
        string? color,
        bool noGit,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await stderr.WriteLineAsync("workspace init: path argument is required.").ConfigureAwait(false);
            return 64;
        }

        var fullPath = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"workspace init: couldn't create '{fullPath}': {ex.Message}").ConfigureAwait(false);
            return 73;
        }

        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            await stderr.WriteLineAsync($"workspace init: directory '{fullPath}' is not empty. Refusing to clobber existing content. Move the existing files aside or pick an empty directory.").ConfigureAwait(false);
            return 65;
        }

        // Folder skeleton — every per-entity bucket the per-entity
        // file format (#148) reads from. Empty subdirectories are kept
        // discoverable by dropping a .gitkeep so `git add .` after init
        // commits them.
        foreach (var sub in WorkspaceSubdirs)
        {
            var subPath = Path.Combine(fullPath, sub);
            Directory.CreateDirectory(subPath);
            await File.WriteAllTextAsync(Path.Combine(subPath, ".gitkeep"), string.Empty, ct).ConfigureAwait(false);
        }

        // workspace.json manifest. Schema version mirrors the .bww file
        // version (#58 Phase 1) so the two formats stay in lockstep.
        var resolvedName = string.IsNullOrWhiteSpace(displayName)
            ? new DirectoryInfo(fullPath).Name
            : displayName!.Trim();
        var resolvedColor = string.IsNullOrWhiteSpace(color)
            ? "#6366f1"
            : color!.Trim();
        var manifest = new
        {
            workspaceFormatVersion = 1,
            id = $"ws_{Guid.NewGuid().ToString("N")[..10]}",
            name = resolvedName,
            color = resolvedColor,
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            includeAllEnvironments = false,
            includedEnvironmentIds = Array.Empty<string>(),
            pluginPins = new Dictionary<string, string>(),
        };
        var manifestJson = JsonSerializer.Serialize(manifest, IndentedJsonOpts);
        await File.WriteAllTextAsync(Path.Combine(fullPath, "workspace.json"),
            manifestJson + Environment.NewLine, ct).ConfigureAwait(false);

        // .gitignore — excludes #151's secret files + the cache dir so
        // a fresh checkout doesn't carry per-machine state. The
        // 'secrets/' folder itself is committed (so the directory
        // structure is visible) but its contents are not.
        var gitignore = string.Join('\n', GitignoreLines) + Environment.NewLine;
        await File.WriteAllTextAsync(Path.Combine(fullPath, ".gitignore"),
            gitignore, ct).ConfigureAwait(false);

        await stdout.WriteLineAsync($"Initialised workspace at {fullPath}").ConfigureAwait(false);
        await stdout.WriteLineAsync("  → workspace.json (manifest, schema v1)").ConfigureAwait(false);
        await stdout.WriteLineAsync("  → .gitignore (secrets + cache excluded)").ConfigureAwait(false);
        await stdout.WriteLineAsync("  → environments/ collections/ recordings/ scripts/ flows/ secrets/ (empty)").ConfigureAwait(false);

        if (noGit)
        {
            await stdout.WriteLineAsync("  → skipping `git init` (--no-git)").ConfigureAwait(false);
        }
        else if (await TryGitInitAsync(fullPath, stdout, stderr, ct).ConfigureAwait(false))
        {
            await stdout.WriteLineAsync("  → git init done — first commit pending").ConfigureAwait(false);
            await stdout.WriteLineAsync($"\nNext: cd {Path.GetRelativePath(Environment.CurrentDirectory, fullPath)} && git add . && git commit -m \"Initial workspace\"").ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync("  → `git init` unavailable (git not on PATH) — skipping").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<bool> TryGitInitAsync(
        string workspacePath, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("init");
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Edge-case coverage for <see cref="WorkspaceCommand"/> beyond the
/// happy-path scenarios already pinned by
/// <see cref="WorkspaceCommandTests"/>. Closes the line-coverage gaps
/// flagged after the #149 / #151 land:
///
/// <list type="bullet">
///   <item><c>Build()</c> + <c>BuildInitCommand()</c> shape (option
///   descriptions, subcommand wiring, action callback path through
///   <c>System.CommandLine</c>) — the existing tests only call
///   <c>RunInitAsync</c> directly and never touch the factory side.</item>
///   <item><c>git init</c> success branch — the existing tests pass
///   <c>--no-git: true</c> for hermeticity, so the
///   <c>TryGitInitAsync</c> path stays cold.</item>
///   <item>Stdout artefact diagnostics (the four "→" lines printed on
///   every successful init) — without these asserts a future refactor
///   that drops the "Initialised workspace at &lt;path&gt;" header,
///   the workspace.json hint, or the .gitignore hint passes silently.</item>
///   <item>Whitespace normalisation on <c>--name</c> and <c>--color</c>
///   (trim → default fallback / trim → keep) — the original tests only
///   covered "value present" vs "value null".</item>
///   <item>Filesystem-failure path — invalid path triggers the
///   <c>Directory.CreateDirectory</c> catch, returning EX_CANTCREAT (73)
///   with a stderr diagnostic.</item>
///   <item>Manifest <c>id</c> uniqueness across two consecutive inits.</item>
/// </list>
///
/// Uses the output-capture pattern landed in commit
/// <c>1bad949</c> ("strengthen MockCommandTests with output-capture
/// assertions") — per-test <see cref="StringWriter"/> pair threaded
/// through the CLI's TextWriter overloads, with concrete substring
/// asserts on the actual diagnostic copy rather than exit codes alone.
/// </summary>
/// <remarks>
/// Joins the <c>CwdSerialised</c> non-parallel collection: the git-init
/// success test toggles the process-global <see cref="Environment.CurrentDirectory"/>
/// to <c>_tempRoot</c> and its <see cref="Dispose"/> deletes that dir. Left
/// unserialised, that races with any sibling calling
/// <c>WebApplication.CreateSlimBuilder()</c> (which reads the cwd as its
/// content root) — the sibling then throws "content root does not exist" when
/// this class's cleanup removes the dir. See <c>CwdSerialisedCollectionDefinition</c>.
/// </remarks>
[Collection("CwdSerialised")]
public sealed class WorkspaceCommandEdgeCasesTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceCommandEdgeCasesTests()
    {
        // Directory.CreateTempSubdirectory gives us a guaranteed-unique
        // dir under %TEMP%. Each test creates a fresh subdir below
        // _tempRoot so parallel runs and post-test cleanup don't cross
        // streams.
        _tempRoot = Directory.CreateTempSubdirectory("bowire-workspace-tests-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup — temp will reclaim eventually */ }
    }

    // ---------------------------------------------------------------
    // Build() factory + System.CommandLine wiring
    // ---------------------------------------------------------------

    [Fact]
    public void Build_returns_workspace_command_with_full_subcommand_set()
    {
        // Pins the parent-command shape: name, description hint, and
        // the four known subcommands as of #149 closeout (init +
        // migrate-format + export + import). When a future phase adds
        // a new verb, this assertion is the forcing function for an
        // updated subcommand inventory.
        var workspace = WorkspaceCommand.Build();

        Assert.Equal("workspace", workspace.Name);
        Assert.Contains("git-backed", workspace.Description, StringComparison.OrdinalIgnoreCase);
        var names = workspace.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("init", names);
        Assert.Contains("migrate-format", names);
        Assert.Contains("export", names);
        Assert.Contains("import", names);
        Assert.Equal(4, workspace.Subcommands.Count);
    }

    [Fact]
    public void Build_init_subcommand_declares_path_argument_and_three_options()
    {
        // Locks down the flag surface so a rename of --no-git, --name,
        // or --color fails this test rather than silently breaking
        // muscle memory + docs links.
        var workspace = WorkspaceCommand.Build();
        var init = workspace.Subcommands.Single(s => s.Name == "init");

        Assert.Contains(init.Arguments, a => a.Name == "path");

        var optionNames = init.Options.Select(o => o.Name).ToHashSet();
        Assert.Contains("--name", optionNames);
        Assert.Contains("--color", optionNames);
        Assert.Contains("--no-git", optionNames);

        // The init subcommand description spells out the "drops the
        // canonical folder skeleton" promise — guards the help copy.
        Assert.Contains("workspace.json", init.Description);
        Assert.Contains(".gitignore", init.Description);
    }

    [Fact]
    public async Task Build_parse_then_invoke_runs_init_action_end_to_end()
    {
        // Drives the SetAction callback registered in BuildInitCommand
        // by going through System.CommandLine's Parse/Invoke pipeline.
        // That's the only path that exercises the
        //   pr.GetValue(pathArg/nameOpt/colorOpt/noGitOpt) + lambda
        // glue between the Command tree and RunInitAsync.
        var workspace = WorkspaceCommand.Build();
        var path = SafePath.Combine(_tempRoot, "via-parse");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var parse = workspace.Parse(new[] { "init", path, "--no-git", "--name", "Parsed", "--color", "#abcdef" });
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());
        Assert.Contains("Initialised workspace at", stdout.ToString());

        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Parsed", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("#abcdef", doc.RootElement.GetProperty("color").GetString());
    }

    // ---------------------------------------------------------------
    // Stdout diagnostic copy — the four "→" lines + header
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunInitAsync_prints_diagnostic_summary_for_each_artifact()
    {
        // Without these asserts a future refactor could drop the
        // workspace.json / .gitignore / folder-skeleton hints and
        // still return exit 0. The "Initialised workspace at" header
        // also doubles as a "did the success branch actually run?"
        // anchor.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "diag");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = stdout.ToString();
        Assert.Contains("Initialised workspace at", output);
        Assert.Contains("workspace.json", output);
        Assert.Contains("manifest, schema v1", output);
        Assert.Contains(".gitignore", output);
        Assert.Contains("secrets + cache excluded", output);
        Assert.Contains("environments/ collections/ recordings/ scripts/ flows/ secrets/", output);
    }

    [Fact]
    public async Task RunInitAsync_with_no_git_prints_skipping_diagnostic()
    {
        // The --no-git branch must surface the "why" so operators
        // running `bowire workspace init . --no-git` inside an
        // existing repo know the trailing `git init` was suppressed
        // by their flag rather than by an environment problem.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "skip-git");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = stdout.ToString();
        Assert.Contains("skipping", output);
        Assert.Contains("--no-git", output);
    }

    // ---------------------------------------------------------------
    // git init success path (only when git is on PATH — guarded so
    // CI runners without git don't fail; the assertion flips to a
    // "git unavailable" stdout match if so).
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunInitAsync_runs_git_init_when_no_git_flag_omitted()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "with-git");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: null,
            noGit: false, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = stdout.ToString();
        // TryGitInitAsync returns true → "git init done" stdout +
        // an actual .git/ dir created in the workspace.
        // Returns false → "git init unavailable" stdout, no .git/.
        // Either branch is fine; the test exercises the dispatch.
        var gitDir = SafePath.Combine(path, ".git");
        if (Directory.Exists(gitDir))
        {
            Assert.Contains("git init done", output);
            Assert.Contains("first commit pending", output);
            // The "Next:" hint should print on success.
            Assert.Contains("git add . && git commit", output);
        }
        else
        {
            // Git not on PATH or git init crashed silently — must
            // surface the fallback diagnostic so operators know why
            // the workspace isn't versioned.
            Assert.Contains("git init", output);
            Assert.Contains("unavailable", output);
        }
    }

    // ---------------------------------------------------------------
    // Whitespace normalisation on --name and --color
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunInitAsync_whitespace_only_name_falls_back_to_directory_basename()
    {
        // string.IsNullOrWhiteSpace short-circuits the trim path and
        // restores the directory-basename default. Without this test
        // a future "trim then accept anything non-null" refactor
        // would land "" or "   " as the workspace name.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-fallback-name");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: "   ", color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ws-fallback-name", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RunInitAsync_whitespace_only_color_falls_back_to_default()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-default-color");

        var rc = await WorkspaceCommand.RunInitAsync(path, displayName: null, color: "  \t  ",
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        // Default accent color from the spec — Indigo 500 / #6366f1.
        Assert.Equal("#6366f1", doc.RootElement.GetProperty("color").GetString());
    }

    [Fact]
    public async Task RunInitAsync_trims_surrounding_whitespace_from_name_and_color()
    {
        // The trim branch (not the IsNullOrWhiteSpace fallback) —
        // value is non-empty after trim so it survives + is stored
        // without padding.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-trimmed");

        var rc = await WorkspaceCommand.RunInitAsync(path,
            displayName: "   Payments   ", color: "  #22c55e  ",
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Payments", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("#22c55e", doc.RootElement.GetProperty("color").GetString());
    }

    [Fact]
    public async Task RunInitAsync_rejects_whitespace_only_path_argument()
    {
        // Mirrors the empty-string path test but exercises the
        // IsNullOrWhiteSpace branch with a non-zero-length string.
        // Bowire returns EX_USAGE (64) so the shell distinguishes
        // user error from filesystem failure (73) and clobber-guard
        // (65).
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await WorkspaceCommand.RunInitAsync(path: "   ",
            displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(64, rc);
        Assert.Contains("path argument", stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    // ---------------------------------------------------------------
    // Filesystem failure — Directory.CreateDirectory throws
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunInitAsync_returns_cantcreate_when_path_is_a_file()
    {
        // CreateDirectory on a path whose parent already exists as
        // a *file* throws IOException ("Cannot create [...] because
        // a file with the same name already exists"). That's the
        // realistic shape of the catch branch — exit 73 + stderr
        // diagnostic, no workspace.json materialised.
        var blocker = SafePath.Combine(_tempRoot, "blocker-file");
        await File.WriteAllTextAsync(blocker, "I am a file, not a dir",
            TestContext.Current.CancellationToken);
        var wanted = SafePath.Combine(blocker, "would-be-workspace");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunInitAsync(wanted,
            displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(73, rc);
        var err = stderr.ToString();
        Assert.Contains("workspace init:", err);
        Assert.Contains("couldn't create", err);
        // The blocker file is untouched.
        Assert.Equal("I am a file, not a dir",
            await File.ReadAllTextAsync(blocker, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // Manifest invariants across two inits
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunInitAsync_two_consecutive_inits_yield_distinct_workspace_ids()
    {
        // Every init should mint a fresh ws_<10char> id (Guid-derived).
        // Without this assert a future refactor that hard-codes or
        // reuses an id slips through.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var a = SafePath.Combine(_tempRoot, "id-a");
        var b = SafePath.Combine(_tempRoot, "id-b");

        await WorkspaceCommand.RunInitAsync(a, null, null, noGit: true, stdout, stderr,
            TestContext.Current.CancellationToken);
        await WorkspaceCommand.RunInitAsync(b, null, null, noGit: true, stdout, stderr,
            TestContext.Current.CancellationToken);

        using var docA = JsonDocument.Parse(
            await File.ReadAllTextAsync(SafePath.Combine(a, "workspace.json"),
                TestContext.Current.CancellationToken));
        using var docB = JsonDocument.Parse(
            await File.ReadAllTextAsync(SafePath.Combine(b, "workspace.json"),
                TestContext.Current.CancellationToken));

        var idA = docA.RootElement.GetProperty("id").GetString();
        var idB = docB.RootElement.GetProperty("id").GetString();
        Assert.NotNull(idA);
        Assert.NotNull(idB);
        Assert.NotEqual(idA, idB);
        Assert.StartsWith("ws_", idA);
        Assert.StartsWith("ws_", idB);
        // 3-char prefix + 10-char guid slice = 13 chars total.
        Assert.Equal(13, idA!.Length);
        Assert.Equal(13, idB!.Length);
    }

    [Fact]
    public async Task RunInitAsync_succeeds_when_path_is_existing_empty_directory()
    {
        // CreateDirectory is a no-op on an existing empty dir;
        // EnumerateFileSystemEntries returns nothing → init proceeds.
        // Exercises the "user pre-created the dir" muscle-memory flow.
        var path = SafePath.Combine(_tempRoot, "preexisting-empty");
        Directory.CreateDirectory(path);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await WorkspaceCommand.RunInitAsync(path,
            displayName: null, color: null,
            noGit: true, stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.Empty(stderr.ToString());
        Assert.True(File.Exists(SafePath.Combine(path, "workspace.json")));
    }

    [Fact]
    public async Task RunInitAsync_manifest_includes_createdAt_within_recent_window()
    {
        // createdAt is a Unix-millis timestamp captured at init time.
        // Asserting it lands within ±60s of "now" pins the field
        // semantics — a future refactor that switches to seconds or
        // ISO-8601 or zeros it out fails this test.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-timestamp");

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rc = await WorkspaceCommand.RunInitAsync(path, null, null, noGit: true,
            stdout, stderr, TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(0, rc);
        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var createdAt = doc.RootElement.GetProperty("createdAt").GetInt64();
        Assert.InRange(createdAt, before, after);
    }

    [Fact]
    public async Task RunInitAsync_manifest_includes_includedEnvironmentIds_as_empty_array()
    {
        // The two collection-shaped properties on the manifest —
        // includedEnvironmentIds (array) + pluginPins (object) — are
        // serialised as JSON containers even when empty. Guards
        // against a future "drop empty collections" PR breaking the
        // workbench's deserialiser, which expects the keys to exist.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-containers");

        await WorkspaceCommand.RunInitAsync(path, null, null, noGit: true,
            stdout, stderr, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(SafePath.Combine(path, "workspace.json"),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("includedEnvironmentIds");
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);
        Assert.Equal(0, ids.GetArrayLength());
        var pins = doc.RootElement.GetProperty("pluginPins");
        Assert.Equal(JsonValueKind.Object, pins.ValueKind);
        Assert.Empty(pins.EnumerateObject());
    }

    [Fact]
    public async Task RunInitAsync_gitignore_carries_phase1_secret_separation_banner()
    {
        // The .gitignore template's leading comment carries the
        // #151 issue reference — the workbench's "explain this file"
        // tooltip surfaces that header to operators. Without an
        // explicit assert the copy can drift silently.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var path = SafePath.Combine(_tempRoot, "ws-banner");

        await WorkspaceCommand.RunInitAsync(path, null, null, noGit: true,
            stdout, stderr, TestContext.Current.CancellationToken);

        var gitignore = await File.ReadAllTextAsync(SafePath.Combine(path, ".gitignore"),
            TestContext.Current.CancellationToken);
        Assert.Contains("Phase 1", gitignore);
        Assert.Contains("#151", gitignore);
        // Trailing newline guarantee — `git status` shows files
        // cleanly when the .gitignore ends with a newline.
        Assert.EndsWith(Environment.NewLine, gitignore);
    }

    [Fact]
    public async Task RunInitAsync_resolves_relative_path_against_current_directory()
    {
        // Path.GetFullPath happens before the empty-check, so a
        // relative path like "./inner" is materialised under cwd.
        // Use a temp cwd so we don't pollute the test runner's
        // working directory.
        var originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempRoot;
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var rc = await WorkspaceCommand.RunInitAsync(
                path: "./relative-init",
                displayName: null, color: null, noGit: true,
                stdout, stderr, TestContext.Current.CancellationToken);

            Assert.Equal(0, rc);
            var resolved = SafePath.Combine(_tempRoot, "relative-init");
            Assert.True(File.Exists(SafePath.Combine(resolved, "workspace.json")));
            // The diagnostic should print the resolved full path —
            // not the literal "./relative-init" the user typed.
            Assert.Contains(Path.GetFullPath(resolved), stdout.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }
}

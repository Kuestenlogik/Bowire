// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Kuestenlogik.Bowire.Workspace.Git;

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
            "Manage Bowire workspaces — init a git-backed workspace directory (#147 / #149), migrate a legacy bundle-shaped workspace to the per-entity file layout (#196 Phase 2.2), or export/import the workspace state as a single JSON file (#149).");
        workspace.Add(BuildInitCommand());
        workspace.Add(BuildMigrateFormatCommand());
        workspace.Add(BuildExportCommand());
        workspace.Add(BuildImportCommand());
        return workspace;
    }

    // ---------- export / import (#149) ----------

    /// <summary>
    /// Current canonical .bww format version (#282 unified shape).
    /// Readers migrate anything older to this version in-memory.
    /// </summary>
    public const int CanonicalFormatVersion = 2;

    /// <summary>
    /// Format version the writer currently emits. Stays at the legacy
    /// value until #282 A2 cuts the writer over to <see cref="CanonicalFormatVersion"/>.
    /// </summary>
    public const int ExportFormatVersion = 1;

    /// <summary>
    /// Legacy CLI export shape — v1 used <c>workspaceFormatVersion</c>
    /// + top-level per-kind arrays without a <c>format</c> header or
    /// <c>workspace</c> identity wrapper. Detected + migrated on
    /// read in <see cref="RunImportAsync"/> through v2.x; the migration
    /// shim is retired in v3.0.0 (#283).
    /// </summary>
    public const int LegacyCliExportFormatVersion = 1;

    private static readonly string[] ExportEntityKinds =
        ["environments", "collections", "recordings", "scripts", "flows"];

    /// <summary>
    /// Data keys present in the v2 envelope's <c>data</c> sub-object
    /// (full superset across browser-mode + disk-mode workspaces).
    /// </summary>
    private static readonly string[] V2DataKeys =
        ["urls", "urlMeta", "environments", "activeEnvironmentId",
         "globals", "collections", "recordings", "scripts", "flows", "presets",
         // #290 — Request-builder history (browser-only; disk exporters write []).
         "requestBuilderHistory"];

    private static Command BuildExportCommand()
    {
        var export = new Command("export",
            "Read every entity from a per-entity workspace directory and write a single self-contained JSON file. Round-trips through 'workspace import' without touching ~/.bowire/. Useful for CI / scripted setup, archiving, or shipping a workspace snapshot.");

        var fromOpt = new Option<string?>("--from")
        {
            Description = "Workspace storage root to read from. When omitted, the current directory is used. The directory must contain at least one of the per-entity buckets (environments/, collections/, recordings/, scripts/, flows/)."
        };
        var outputArg = new Argument<string>("path")
        {
            Description = "Output file path. The exporter writes a single indented JSON document with workspaceFormatVersion + one array per entity kind."
        };
        export.Add(fromOpt);
        export.Add(outputArg);

        export.SetAction((pr, ct) =>
        {
            var output = pr.GetValue(outputArg)!;
            var from = pr.GetValue(fromOpt);
            return RunExportAsync(
                from ?? Directory.GetCurrentDirectory(),
                output,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error,
                ct);
        });
        return export;
    }

    private static Command BuildImportCommand()
    {
        var import = new Command("import",
            "Materialise a workspace export (the single-JSON shape 'workspace export' produces) into a target directory as the per-entity layout. Existing entries with the same id are overwritten; entries the export doesn't carry are left alone.");

        var toOpt = new Option<string?>("--to")
        {
            Description = "Target workspace directory to write into. When omitted, the current directory is used. Created if missing."
        };
        var inputArg = new Argument<string>("path")
        {
            Description = "Path to the .json export file produced by 'workspace export'."
        };
        import.Add(toOpt);
        import.Add(inputArg);

        import.SetAction((pr, ct) =>
        {
            var input = pr.GetValue(inputArg)!;
            var to = pr.GetValue(toOpt);
            return RunImportAsync(
                input,
                to ?? Directory.GetCurrentDirectory(),
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error,
                ct);
        });
        return import;
    }

    /// <summary>
    /// #282 — Detect + migrate legacy workspace-export shapes to the
    /// v2 canonical envelope. Two pre-v2 shapes ship in v2.0 and
    /// must keep working through v2.x; the shim retires in v3.0.0
    /// (#283).
    /// <list type="bullet">
    ///   <item><b>CLI-v1</b>: no <c>format</c> header,
    ///     <c>workspaceFormatVersion: 1</c> + top-level per-kind arrays.</item>
    ///   <item><b>UI-v1</b>: <c>format: 'bowire-workspace', version: 1,
    ///     workspace, data</c> — same envelope shape as v2, only the
    ///     <c>version</c> field differs.</item>
    /// </list>
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject MigrateLegacyWorkspaceShape(
        System.Text.Json.Nodes.JsonObject root)
    {
        if (root is null) return new System.Text.Json.Nodes.JsonObject();

        // Already v2 — pass through unchanged.
        if ((string?)root["format"] == "bowire-workspace"
            && root["version"] is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<int>(out var ver) && ver == CanonicalFormatVersion)
        {
            return root;
        }

        // CLI-v1: no format header, workspaceFormatVersion present.
        if ((string?)root["format"] != "bowire-workspace"
            && root["workspaceFormatVersion"] is not null)
        {
            var data = new System.Text.Json.Nodes.JsonObject();
            data["urls"] = new System.Text.Json.Nodes.JsonArray();
            data["urlMeta"] = new System.Text.Json.Nodes.JsonObject();
            data["activeEnvironmentId"] = null;
            data["globals"] = new System.Text.Json.Nodes.JsonObject();
            data["presets"] = new System.Text.Json.Nodes.JsonObject();
            foreach (var kind in ExportEntityKinds)
            {
                data[kind] = root[kind] is System.Text.Json.Nodes.JsonArray arr
                    ? (System.Text.Json.Nodes.JsonNode)arr.DeepClone()
                    : new System.Text.Json.Nodes.JsonArray();
            }
            var workspaceMeta = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "Imported",
                ["color"] = "#6366f1",
                ["description"] = "",
                ["pluginPins"] = null
            };
            return new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = "bowire-workspace",
                ["version"] = CanonicalFormatVersion,
                ["exportedAt"] = (string?)root["exportedAt"]
                    ?? System.DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["workspace"] = workspaceMeta,
                ["data"] = data,
                ["_migratedFrom"] = "cli-v1"
            };
        }

        // UI-v1: format header present, version === 1.
        if ((string?)root["format"] == "bowire-workspace"
            && root["version"] is System.Text.Json.Nodes.JsonValue uiV
            && uiV.TryGetValue<int>(out var uiVer) && uiVer == 1)
        {
            var existingData = root["data"] as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
            var data2 = new System.Text.Json.Nodes.JsonObject();
            foreach (var key in V2DataKeys)
            {
                if (existingData[key] is { } node)
                {
                    data2[key] = node.DeepClone();
                }
                else
                {
                    // Backfill missing buckets with empty containers so
                    // downstream code can iterate without null checks.
                    data2[key] = key switch
                    {
                        "urlMeta" or "globals" or "presets" => new System.Text.Json.Nodes.JsonObject(),
                        "activeEnvironmentId" => null,
                        _ => new System.Text.Json.Nodes.JsonArray()
                    };
                }
            }
            var workspace2 = root["workspace"]?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = "bowire-workspace",
                ["version"] = CanonicalFormatVersion,
                ["exportedAt"] = (string?)root["exportedAt"]
                    ?? System.DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["workspace"] = workspace2,
                ["data"] = data2,
                ["_migratedFrom"] = "ui-v1"
            };
        }

        // Unrecognised shape — return as-is and let the downstream
        // validator throw the canonical error.
        return root;
    }

    // Internal so unit tests exercise the pipeline without spinning up
    // System.CommandLine. Mirrors RunMigrateFormatAsync's sysexits-style
    // exit codes — 0 success, 64 EX_USAGE, 65 EX_DATAERR, 66 EX_NOINPUT,
    // 70 generic failure.
    internal static async Task<int> RunExportAsync(
        string sourceDir,
        string outputPath,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            await stderr.WriteLineAsync("workspace export: output path is required.").ConfigureAwait(false);
            return 64;
        }

        var fullSource = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSource))
        {
            await stderr.WriteLineAsync($"workspace export: source directory '{fullSource}' does not exist.").ConfigureAwait(false);
            return 66;
        }

        var store = new Kuestenlogik.Bowire.Workspace.Git.FileEntityStore(fullSource);

        // #282 A2 — emit the v2 canonical envelope. Workspace identity
        // pulled from workspace.json (if present), per-entity arrays
        // nested under `data`, globals lifted from globals.json. Disk-
        // only workspaces don't have urls / urlMeta / favorites / etc.
        // — those buckets ship as empty defaults so readers can iterate
        // the v2 superset without null checks.
        var workspaceIdentity = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = new DirectoryInfo(fullSource).Name,
            ["color"] = "#6366f1",
            ["description"] = "",
            ["pluginPins"] = null
        };
        var manifestPath = Path.Combine(fullSource, "workspace.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestRaw = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                if (System.Text.Json.Nodes.JsonNode.Parse(manifestRaw) is System.Text.Json.Nodes.JsonObject manifest)
                {
                    if (manifest["id"] is { } mid) workspaceIdentity["id"] = mid.DeepClone();
                    if (manifest["name"] is { } mname) workspaceIdentity["name"] = mname.DeepClone();
                    if (manifest["color"] is { } mcolor) workspaceIdentity["color"] = mcolor.DeepClone();
                    if (manifest["description"] is { } mdesc) workspaceIdentity["description"] = mdesc.DeepClone();
                    if (manifest["pluginPins"] is { } mpins) workspaceIdentity["pluginPins"] = mpins.DeepClone();
                }
            }
            catch (JsonException)
            {
                // workspace.json malformed — proceed with defaults.
                // (The data export is more valuable than blocking on
                // a manifest read failure.)
            }
        }

        var data = new System.Text.Json.Nodes.JsonObject();
        // Seed every v2 data bucket with an empty default so readers
        // never see undefined fields. Disk-only buckets get filled
        // from per-entity files below; browser-only buckets stay [].
        foreach (var key in V2DataKeys)
        {
            data[key] = key switch
            {
                "urlMeta" or "globals" or "presets" => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject(),
                "activeEnvironmentId" => null,
                _ => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray()
            };
        }

        // globals.json (per-entity file at workspace root).
        var globalsPath = Path.Combine(fullSource, "globals.json");
        if (File.Exists(globalsPath))
        {
            try
            {
                var globalsRaw = await File.ReadAllTextAsync(globalsPath, ct).ConfigureAwait(false);
                if (System.Text.Json.Nodes.JsonNode.Parse(globalsRaw) is System.Text.Json.Nodes.JsonObject globalsObj)
                {
                    data["globals"] = globalsObj;
                }
            }
            catch (JsonException) { /* skip on malformed */ }
        }

        var perKindCount = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var kind in ExportEntityKinds)
            {
                var ids = await store.ListAsync(kind, ct).ConfigureAwait(false);
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var id in ids)
                {
                    var json = await store.LoadAsync(kind, id, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(json)) continue;
                    arr.Add(System.Text.Json.Nodes.JsonNode.Parse(json));
                }
                data[kind] = arr;
                perKindCount[kind] = arr.Count;
            }
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"workspace export: a per-entity file is not valid JSON: {ex.Message}").ConfigureAwait(false);
            return 65;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"workspace export: read failed: {ex.Message}").ConfigureAwait(false);
            return 70;
        }

        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "bowire-workspace",
            ["version"] = CanonicalFormatVersion,
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["workspace"] = workspaceIdentity,
            ["data"] = data
        };

        var fullOutput = Path.GetFullPath(outputPath);
        var outDir = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        try
        {
            await File.WriteAllTextAsync(fullOutput,
                root.ToJsonString(IndentedJsonOpts) + Environment.NewLine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await stderr.WriteLineAsync($"workspace export: cannot write '{fullOutput}': {ex.Message}").ConfigureAwait(false);
            return 70;
        }

        await stdout.WriteLineAsync($"Exported workspace at {fullSource} to {fullOutput}").ConfigureAwait(false);
        var total = 0;
        foreach (var kind in ExportEntityKinds)
        {
            var count = perKindCount.TryGetValue(kind, out var c) ? c : 0;
            total += count;
            if (count == 0)
            {
                await stdout.WriteLineAsync($"  · {kind}: 0").ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync($"  → {kind}: {count}").ConfigureAwait(false);
            }
        }
        await stdout.WriteLineAsync($"  → {total} entity(ies) exported total.").ConfigureAwait(false);
        return 0;
    }

    internal static async Task<int> RunImportAsync(
        string inputPath,
        string targetDir,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            await stderr.WriteLineAsync("workspace import: input path is required.").ConfigureAwait(false);
            return 64;
        }
        var fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
        {
            await stderr.WriteLineAsync($"workspace import: input file '{fullInput}' does not exist.").ConfigureAwait(false);
            return 66;
        }

        System.Text.Json.Nodes.JsonObject root;
        try
        {
            var raw = await File.ReadAllTextAsync(fullInput, ct).ConfigureAwait(false);
            root = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject
                ?? throw new JsonException("Export root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"workspace import: '{fullInput}' is not a valid workspace export: {ex.Message}").ConfigureAwait(false);
            return 65;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"workspace import: cannot read '{fullInput}': {ex.Message}").ConfigureAwait(false);
            return 70;
        }

        // #282 — Detect + migrate legacy .bww shapes to the v2
        // canonical schema. Two pre-v2 shapes ship in v2.0:
        //   1. UI-v1: { format: 'bowire-workspace', version: 1, workspace, data }
        //   2. CLI-v1: { workspaceFormatVersion: 1, ..., environments[], … }
        // Both are migrated to v2 in-memory; the rest of the importer
        // sees v2 only. The shim is retired in v3.0.0 (#283).
        root = MigrateLegacyWorkspaceShape(root);

        // Refuse exports from a future format we don't understand. Same
        // shape as RecordingFormatVersion's check elsewhere. Check
        // happens AFTER the migration shim has rewritten legacy v1
        // shapes into v2, so the post-migration version is what we
        // gate on.
        if (root["version"] is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<int>(out var vers)
            && vers > CanonicalFormatVersion)
        {
            await stderr.WriteLineAsync(
                $"workspace import: export was written under format version {vers}, " +
                $"this build supports up to {CanonicalFormatVersion}. Update Bowire and retry.").ConfigureAwait(false);
            return 65;
        }

        var fullTarget = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(fullTarget);
        var store = new Kuestenlogik.Bowire.Workspace.Git.FileEntityStore(fullTarget);

        // v2 envelope nests per-kind arrays inside `data`; legacy CLI
        // shapes pre-migration had them at the top level. After the
        // shim above, the data sub-object is always present.
        var v2Data = root["data"] as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();

        var perKindCount = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var kind in ExportEntityKinds)
            {
                if (v2Data[kind] is not System.Text.Json.Nodes.JsonArray arr) continue;
                var written = 0;
                foreach (var entry in arr)
                {
                    if (entry is not System.Text.Json.Nodes.JsonObject obj) continue;
                    var id = (string?)obj["id"];
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    await store.SaveAsync(kind, id!, obj.ToJsonString(IndentedJsonOpts), ct).ConfigureAwait(false);
                    written++;
                }
                perKindCount[kind] = written;
            }
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"workspace import: per-entity JSON write failed: {ex.Message}").ConfigureAwait(false);
            return 65;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"workspace import: write failed: {ex.Message}").ConfigureAwait(false);
            return 70;
        }

        await stdout.WriteLineAsync($"Imported workspace from {fullInput} into {fullTarget}").ConfigureAwait(false);
        var total = 0;
        foreach (var kind in ExportEntityKinds)
        {
            var count = perKindCount.TryGetValue(kind, out var c) ? c : 0;
            total += count;
            if (count == 0)
            {
                await stdout.WriteLineAsync($"  · {kind}: 0").ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync($"  → {kind}: {count}").ConfigureAwait(false);
            }
        }
        await stdout.WriteLineAsync($"  → {total} entity(ies) imported total.").ConfigureAwait(false);
        return 0;
    }

    private static Command BuildMigrateFormatCommand()
    {
        var migrate = new Command("migrate-format",
            "Convert a workspace from the legacy bundle layout (one <entityKind>.json per kind) into the per-entity file layout the git-backed runtime reads through. Idempotent: re-running on an already-migrated workspace is a no-op. The original bundle files are renamed to .legacy so an operator can verify the per-entity files before deleting them.");

        var pathArg = new Argument<string>("path")
        {
            Description = "Directory containing the workspace to migrate. Existing per-entity files are preserved; only legacy <entityKind>.json bundles are converted."
        };
        migrate.Add(pathArg);

        migrate.SetAction((pr, ct) =>
        {
            var path = pr.GetValue(pathArg)!;
            return RunMigrateFormatAsync(path,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct);
        });

        return migrate;
    }

    // Internal so unit tests can exercise the migration pipeline
    // without spinning up System.CommandLine.
    internal static async Task<int> RunMigrateFormatAsync(
        string path,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await stderr.WriteLineAsync("workspace migrate-format: path argument is required.").ConfigureAwait(false);
            return 64;
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            await stderr.WriteLineAsync($"workspace migrate-format: directory '{fullPath}' does not exist.").ConfigureAwait(false);
            return 66;
        }

        BowireGitWorkspaceMigrationReport report;
        try
        {
            report = await BowireGitWorkspaceMigrator.MigrateAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"workspace migrate-format: a legacy bundle is not valid JSON: {ex.Message}").ConfigureAwait(false);
            return 65;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"workspace migrate-format: migration failed: {ex.Message}").ConfigureAwait(false);
            return 70;
        }

        if (!report.AnyMigrated)
        {
            await stdout.WriteLineAsync($"workspace migrate-format: nothing to do at {fullPath} (already per-entity layout).").ConfigureAwait(false);
            return 0;
        }

        await stdout.WriteLineAsync($"Migrated workspace at {fullPath}").ConfigureAwait(false);
        foreach (var kind in report.Kinds)
        {
            if (!kind.LegacyFound)
            {
                await stdout.WriteLineAsync($"  · {kind.EntityKind}: skipped (no legacy bundle)").ConfigureAwait(false);
                continue;
            }
            await stdout.WriteLineAsync($"  → {kind.EntityKind}: {kind.Migrated} entity(ies) → {kind.EntityKind}/*.json").ConfigureAwait(false);
        }
        await stdout.WriteLineAsync($"  → {report.TotalEntities} entity(ies) migrated total").ConfigureAwait(false);
        await stdout.WriteLineAsync("  → legacy bundles renamed to *.legacy; remove after verifying the per-entity files.").ConfigureAwait(false);

        return 0;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or NotSupportedException)
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
        foreach (var subPath in WorkspaceSubdirs.Select(sub => Path.Combine(fullPath, sub)))
        {
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

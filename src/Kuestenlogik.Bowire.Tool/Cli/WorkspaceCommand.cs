// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;
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
            "Manage Bowire workspaces — init a git-backed workspace directory (#147 / #149), migrate a legacy bundle-shaped workspace to the per-entity file layout (#196 Phase 2.2), migrate a workspace to a checked-in .bowire/project.json manifest (#172), or export/import the workspace state as a single JSON file (#149).");
        workspace.Add(BuildInitCommand());
        workspace.Add(BuildMigrateFormatCommand());
        workspace.Add(BuildMigrateToProjectCommand());
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

    // ---------- migrate --to-project (#172) ----------

    // sysexits.h-style exit codes, matching the project / recording commands
    // so a CI shell can branch on the failure mode without scraping stderr.
    private const int MigrateOk = 0;
    private const int MigrateUsage = 64;     // EX_USAGE — ambiguous / missing --workspace
    private const int MigrateNoInput = 66;   // EX_NOINPUT — workspace not found
    private const int MigrateCantCreat = 73; // EX_CANTCREAT — output exists (no --force) / write failed

    private static Command BuildMigrateToProjectCommand()
    {
        var migrate = new Command("migrate",
            "Convert an existing per-user workspace (~/.bowire/workspaces/<id>) into a checked-in .bowire/project.json manifest (#172). Captures what maps cleanly from the workspace onto the version-controlled convention: sources (distinct server URLs seen across recordings), suites (one per saved collection), and security.auth (the workspace's captured auth recording). Fields with no clean workspace source (a rules file, scan profiles) are reported and omitted rather than invented.");

        var toProjectOpt = new Option<bool>("--to-project")
        {
            Description = "Required verb-flag selecting the project-manifest migration (mirrors the issue's `workspace migrate --to-project`). Present for forward-compatibility with future migrate targets.",
        };
        var workspaceOpt = new Option<string?>("--workspace")
        {
            Description = "Workspace id under ~/.bowire/workspaces/ to migrate. Defaults to the only workspace when exactly one exists; required when several do.",
        };
        var outOpt = new Option<string?>("--out")
        {
            Description = "Directory to write the manifest into (as <out>/.bowire/project.json). Defaults to the current directory.",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Overwrite an existing <out>/.bowire/project.json. Without it, migrate refuses to clobber and exits 73.",
        };
        migrate.Add(toProjectOpt);
        migrate.Add(workspaceOpt);
        migrate.Add(outOpt);
        migrate.Add(forceOpt);

        migrate.SetAction(async (pr, ct) =>
        {
            if (!pr.GetValue(toProjectOpt))
            {
                await pr.InvocationConfiguration.Error.WriteLineAsync(
                    "workspace migrate: pass --to-project to migrate a workspace to a .bowire/project.json manifest.")
                    .ConfigureAwait(false);
                return MigrateUsage;
            }
            return await RunMigrateToProjectAsync(
                pr.GetValue(workspaceOpt),
                pr.GetValue(outOpt) ?? Directory.GetCurrentDirectory(),
                pr.GetValue(forceOpt),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct)
                .ConfigureAwait(false);
        });

        return migrate;
    }

    // Resolves the workspace directory from ~/.bowire (via BowireUserContext),
    // then delegates to the path-based core so the mapping logic is unit-
    // testable against a temp directory without touching the real ~/.bowire.
    internal static async Task<int> RunMigrateToProjectAsync(
        string? workspaceId,
        string outDir,
        bool force,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        string workspacesRoot;
        try
        {
            workspacesRoot = BowireUserContext.GetUserPath("workspaces");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await stderr.WriteLineAsync($"workspace migrate: couldn't resolve the workspaces root: {ex.Message}").ConfigureAwait(false);
            return MigrateNoInput;
        }

        if (!Directory.Exists(workspacesRoot))
        {
            await stderr.WriteLineAsync($"workspace migrate: no workspaces found at {workspacesRoot}.").ConfigureAwait(false);
            return MigrateNoInput;
        }

        string resolvedId;
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            resolvedId = workspaceId.Trim();
        }
        else
        {
            var candidates = Directory.EnumerateDirectories(workspacesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            if (candidates.Count == 0)
            {
                await stderr.WriteLineAsync($"workspace migrate: no workspaces found under {workspacesRoot}.").ConfigureAwait(false);
                return MigrateNoInput;
            }
            if (candidates.Count > 1)
            {
                await stderr.WriteLineAsync(
                    $"workspace migrate: {candidates.Count} workspaces exist — pass --workspace <id> to pick one ({string.Join(", ", candidates)}).").ConfigureAwait(false);
                return MigrateUsage;
            }
            resolvedId = candidates[0]!;
        }

        // Anchor the entity subdirectories under the resolved workspace via the
        // same GetWorkspacePath seam the stores use; the workspace root is the
        // parent of any one of them.
        var probe = BowireUserContext.GetWorkspacePath(resolvedId, storageRoot: null, "collections");
        var wsRoot = Path.GetDirectoryName(probe)!;
        if (!Directory.Exists(wsRoot))
        {
            await stderr.WriteLineAsync($"workspace migrate: workspace '{resolvedId}' not found at {wsRoot}.").ConfigureAwait(false);
            return MigrateNoInput;
        }

        return await MigrateWorkspaceToProjectAsync(wsRoot, resolvedId, outDir, force, stdout, stderr, ct).ConfigureAwait(false);
    }

    // Path-based core: read a workspace directory, build the manifest, write it.
    // Internal so tests exercise the mapping against a temp workspace tree.
    internal static async Task<int> MigrateWorkspaceToProjectAsync(
        string wsRoot,
        string workspaceId,
        string outDir,
        bool force,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (!Directory.Exists(wsRoot))
        {
            await stderr.WriteLineAsync($"workspace migrate: workspace directory '{wsRoot}' does not exist.").ConfigureAwait(false);
            return MigrateNoInput;
        }

        var outPath = Path.Combine(Path.GetFullPath(outDir), BowireProjectLoader.ConventionDirName, BowireProjectLoader.ConventionFileName);
        if (File.Exists(outPath) && !force)
        {
            await stderr.WriteLineAsync($"workspace migrate: '{outPath}' already exists. Re-run with --force to overwrite.").ConfigureAwait(false);
            return MigrateCantCreat;
        }

        var project = new BowireProjectFile
        {
            Schema = BowireProjectFile.SchemaUrl,
            Version = BowireProjectFile.SupportedVersion,
            Name = await ResolveWorkspaceNameAsync(wsRoot, workspaceId, ct).ConfigureAwait(false),
        };

        // sources ← distinct base URLs seen across the workspace's recordings.
        var sourceUrls = await CollectRecordingServerUrlsAsync(wsRoot, ct).ConfigureAwait(false);
        foreach (var url in sourceUrls)
            project.Sources.Add(new BowireProjectSource { Url = url });

        // suites ← one per saved collection, keyed by collection id.
        var collections = await new FileEntityStore(wsRoot).ListAsync("collections", ct).ConfigureAwait(false);
        foreach (var id in collections)
            project.Suites[id] = $"./bowire/suites/{id}.collection.json";

        // security.auth ← the workspace's captured auth recording (first when several).
        var authIds = ListAuthRecordingIds(wsRoot);
        if (authIds.Count > 0)
        {
            project.Security = new BowireProjectSecurity { Auth = $"./bowire/auth/{authIds[0]}.flow.json" };
        }

        // Persist. Validate() should be clean for a machine-authored manifest;
        // surface any problem rather than write a manifest the loader rejects.
        var errors = project.Validate();
        if (errors.Count > 0)
        {
            await stderr.WriteLineAsync("workspace migrate: the migrated manifest failed validation:").ConfigureAwait(false);
            foreach (var error in errors)
                await stderr.WriteLineAsync($"  - {error}").ConfigureAwait(false);
            return MigrateCantCreat;
        }

        try
        {
            var outFolder = Path.GetDirectoryName(outPath)!;
            Directory.CreateDirectory(outFolder);
            await File.WriteAllTextAsync(outPath, project.ToJson() + Environment.NewLine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException or PathTooLongException)
        {
            await stderr.WriteLineAsync($"workspace migrate: cannot write '{outPath}': {ex.Message}").ConfigureAwait(false);
            return MigrateCantCreat;
        }

        await stdout.WriteLineAsync($"Migrated workspace '{workspaceId}' → {outPath}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  → sources: {project.Sources.Count} (distinct server URL(s) from recordings)").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  → suites:  {project.Suites.Count} (one per collection)").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  → security.auth: {(project.Security?.Auth is { } auth ? auth : "(none)")}").ConfigureAwait(false);

        // Fields with no clean workspace source — noted here, not smuggled into
        // the strict-JSON manifest (project.json disallows unknown fields).
        if (authIds.Count > 1)
            await stdout.WriteLineAsync($"  · note: {authIds.Count} auth recordings found; referenced the first ('{authIds[0]}').").ConfigureAwait(false);
        if (project.Sources.Count == 0)
            await stdout.WriteLineAsync("  · note: no recordings carried a server URL — 'sources' left empty.").ConfigureAwait(false);
        await stdout.WriteLineAsync("  · note: 'rules' and 'security.scan' have no workspace source — omit/author them in the manifest as needed.").ConfigureAwait(false);
        await stdout.WriteLineAsync("  · Referenced suite/auth paths are project-relative placeholders; export the collection/auth files into .bowire/ to make them resolve.").ConfigureAwait(false);

        return MigrateOk;
    }

    private static async Task<string?> ResolveWorkspaceNameAsync(string wsRoot, string workspaceId, CancellationToken ct)
    {
        var manifestPath = Path.Combine(wsRoot, "workspace.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    return nameEl.GetString();
                }
            }
            catch (JsonException)
            {
                // Malformed manifest — fall back to the id below.
            }
        }
        return string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
    }

    private static List<string> ListAuthRecordingIds(string wsRoot)
    {
        var dir = Path.Combine(wsRoot, "auth-recordings");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    // Scan every recording JSON in the workspace and collect the distinct base
    // URLs (scheme://host[:port]) that each captured step targeted. Robust to
    // both the flat and chunked on-disk recording layouts — it walks the JSON
    // for any "serverUrl" string rather than binding to one recording shape.
    private static async Task<List<string>> CollectRecordingServerUrlsAsync(string wsRoot, CancellationToken ct)
    {
        var recordingsDir = Path.Combine(wsRoot, "recordings");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(recordingsDir)) return [];

        foreach (var file in Directory.EnumerateFiles(recordingsDir, "*.json", SearchOption.AllDirectories))
        {
            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                CollectServerUrls(doc.RootElement, found);
            }
            catch (JsonException)
            {
                // Skip a corrupt recording file rather than fail the migration.
            }
        }

        return found.ToList();
    }

    private static void CollectServerUrls(JsonElement element, SortedSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "serverUrl", StringComparison.Ordinal)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            into.Add(ToBaseUrl(value));
                    }
                    else
                    {
                        CollectServerUrls(prop.Value, into);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectServerUrls(item, into);
                break;
            default:
                break;
        }
    }

    private static string ToBaseUrl(string raw)
    {
        var trimmed = raw.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : trimmed;
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

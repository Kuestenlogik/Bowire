// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Workspace file support — loads and saves a <c>.bww</c> JSON file
/// from the working directory. The workspace bundles environments,
/// collections, recordings, flows, plugin pins, and URL configuration
/// so the whole setup is portable and shareable via version control.
///
/// File format (#58 Phase 1):
/// <code>
/// {
///   "workspaceFormatVersion": 1,
///   "urls": ["https://api.example.com"],
///   "environments": [ ... ],
///   "globals": { ... },
///   "collections": [ ... ],
///   "recordings": [ ... ],
///   "flows": [ ... ],
///   "pluginPins": { "grpc": "1.5.0", "mqtt": "1.5.0" }
/// }
/// </code>
///
/// The file is read on startup and written back on every save. When
/// no workspace file exists, the endpoints return empty defaults.
/// Missing fields in older files deserialize to their empty default
/// — adding a new field never breaks an existing .bww.
/// </summary>
internal static class BowireWorkspaceEndpoints
{
    // #58 — Current schema version. Increment when the .bww shape
    // changes in a way an older reader would mis-handle; readers can
    // gate behavior on the version field. Serialized as
    // 'workspaceFormatVersion' on disk.
    public const int CurrentFormatVersion = 1;

    // PropertyNamingPolicy = CamelCase pins serialized keys to a
    // stable, conventional shape (urls / environments / collections /
    // recordings / flows / pluginPins / workspaceFormatVersion).
    // PropertyNameCaseInsensitive stays on the read side so old files
    // with PascalCase keys still parse. Records serialize properties
    // in declaration order, giving deterministic key ordering so
    // diffs stay clean across saves.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static string WorkspacePath =>
        Path.Combine(Directory.GetCurrentDirectory(), ".bww");

    public static IEndpointRouteBuilder MapBowireWorkspaceEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/workspace", () =>
        {
            if (!File.Exists(WorkspacePath))
                return Results.Ok(new WorkspaceFile());
            try
            {
                var json = File.ReadAllText(WorkspacePath);
                var ws = JsonSerializer.Deserialize<WorkspaceFile>(json, JsonOpts)
                    ?? new WorkspaceFile();
                return Results.Ok(ws);
            }
            catch
            {
                return Results.Ok(new WorkspaceFile());
            }
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/workspace", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            try
            {
                var ws = JsonSerializer.Deserialize<WorkspaceFile>(body, JsonOpts);
                if (ws is not null)
                {
                    await File.WriteAllTextAsync(WorkspacePath,
                        JsonSerializer.Serialize(ws, JsonOpts));
                }
                return Results.Ok(new { saved = true });
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:workspace:save-failed",
                    title: "Couldn't save workspace",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
            }
        }).ExcludeFromDescription();

        // #127 — folder-open capability + action.
        //
        // The workbench's save-state pill links to the user-folder
        // where the host writes recordings + per-workspace state, so
        // operators can poke around with their normal file manager.
        // STANDALONE ONLY: we refuse to spawn a desktop process from
        // an embedded ASP.NET host because the host process there is
        // typically a production server, not a developer's machine.
        // Hosting Bowire embedded ≠ giving every browser-side workbench
        // user a 'launch GUI process on the server' button.
        //
        // capability probe at boot lets the JS gate the click-handler:
        //   embedded → returns { available: false, reason: 'embedded' }
        //   standalone → { available: true, path: '<resolved root>' }
        endpoints.MapGet($"{basePath}/api/workspace/can-open-folder",
            (IOptions<BowireOptions> opts) =>
        {
            if (opts.Value.Mode != BowireMode.Standalone)
                return Results.Ok(new { available = false, reason = "embedded" });
            return Results.Ok(new { available = true });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/workspace/open-folder",
            (HttpContext ctx, IOptions<BowireOptions> opts, string? workspaceId) =>
        {
            if (opts.Value.Mode != BowireMode.Standalone)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:workspace:open-folder-not-available",
                    title: "Folder open is standalone-only",
                    status: 403,
                    detail: "Bowire refuses to spawn a desktop file-manager process from an embedded host (the host is typically a production server). This endpoint is available in standalone tool mode only.",
                    instance: ctx.Request.Path);
            }

            // Resolve the path: per-workspace if a workspaceId was passed
            // AND it sanitises to a non-empty segment; otherwise the
            // user root (~/.bowire). Sanitisation guards against path
            // traversal — a hostile workspace id like '../../etc' would
            // otherwise resolve to a path outside the user root.
            string target;
            try
            {
                if (!string.IsNullOrWhiteSpace(workspaceId))
                {
                    var sanitised = SanitiseWorkspaceId(workspaceId);
                    target = string.IsNullOrEmpty(sanitised)
                        ? BowireUserContext.GetUserPath("")
                        : BowireUserContext.GetUserPath(Path.Combine("workspaces", sanitised));
                }
                else
                {
                    target = BowireUserContext.GetUserPath("");
                }

                Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:workspace:open-folder-resolve-failed",
                    title: "Couldn't resolve workspace folder",
                    status: 500,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }

            try
            {
                LaunchPlatformFileManager(target);
                return Results.Ok(new { opened = true, path = target });
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:workspace:open-folder-failed",
                    title: "Couldn't launch file manager",
                    status: 500,
                    detail: ex.Message,
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?>
                    {
                        ["path"] = target,
                        ["exceptionType"] = ex.GetType().Name
                    });
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static string SanitiseWorkspaceId(string raw)
    {
        // Keep alnum + '-' + '_'; drop everything else. Mirrors the
        // sanitiser the recording store uses so the two reach the same
        // directory under workspaces/<id>/.
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return new string(chars);
    }

    private static void LaunchPlatformFileManager(string path)
    {
        // Defence-in-depth: the caller already routes path through
        // SanitiseWorkspaceId + BowireUserContext.GetUserPath, but we
        // re-assert here that the resolved absolute path lands under
        // the user root before handing it to the OS.
        var userRoot = Path.GetFullPath(BowireUserContext.GetUserPath(""));
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(userRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to open '{path}': resolved path escapes the user root.");
        }

        // Stronger sanitiser at the sink — every character of the
        // segment past the user root must be in a known-safe class
        // (alnum, '-', '_', '.', or the OS path separator). The
        // earlier SanitiseWorkspaceId pass already enforces alnum +
        // '-' + '_' on the id; the assertion here is the inline,
        // sink-adjacent character-class check that CodeQL's taint
        // tracker recognises as the sanitiser for
        // cs/command-line-injection — the earlier StartsWith guard
        // alone wasn't enough to drop the finding (#46).
        var relative = resolved[userRoot.Length..];
        foreach (var c in relative)
        {
            if (!char.IsLetterOrDigit(c)
                && c != '-' && c != '_' && c != '.'
                && c != Path.DirectorySeparatorChar
                && c != Path.AltDirectorySeparatorChar)
            {
                throw new InvalidOperationException(
                    $"Refusing to open '{path}': resolved path contains a disallowed character ('{c}').");
            }
        }

        // ProcessStartInfo.FileName with UseShellExecute=true asks the
        // OS shell to open the document at the resolved path —
        // Explorer on Windows, Finder on macOS, xdg-open on Linux. No
        // command-line argument string is constructed, so there is no
        // injection surface beyond what the guards above already
        // covered.
        var psi = new ProcessStartInfo
        {
            FileName = resolved,
            UseShellExecute = true,
        };
        Process.Start(psi);
    }

    // #58 — Workspace file schema. Property declaration order doubles
    // as the on-disk JSON key order; keep version first so a stale
    // reader sees the format hint before anything else, then the
    // existing fields in their historical order, then the new ones
    // appended at the end. This minimises diff churn for workspaces
    // that don't use the new fields yet.
    internal sealed record WorkspaceFile
    {
        public int WorkspaceFormatVersion { get; init; } = CurrentFormatVersion;
        public List<string> Urls { get; init; } = [];
        public List<JsonElement> Environments { get; init; } = [];
        public Dictionary<string, string> Globals { get; init; } = new();
        public List<JsonElement> Collections { get; init; } = [];
        // #58 — Recordings stored inline so a `git add .bww` captures
        // the whole project setup in one file. Each entry is a raw
        // JsonElement so the workbench's recording shape (steps,
        // metadata, &c) can evolve without forcing a schema change
        // here.
        public List<JsonElement> Recordings { get; init; } = [];
        // #58 — Flows live inline alongside recordings for the same
        // reason — keep the whole "what we run + how we run it"
        // bundle reviewable in one PR diff.
        public List<JsonElement> Flows { get; init; } = [];
        // #58 — Plugin pins: protocolId → semver string. Lets a
        // workspace declare "this project expects MQTT 1.5.0+ + gRPC
        // 1.5.0+"; the standalone host can warn on mismatch or offer
        // to install missing protocols on open. Empty map = no
        // requirement, current behavior preserved.
        public Dictionary<string, string> PluginPins { get; init; } = new();
    }
}

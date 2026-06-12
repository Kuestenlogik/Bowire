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
/// Workspace file support — loads and saves a <c>.blw</c> JSON file
/// from the working directory. The workspace bundles environments,
/// collections, and URL configuration so the whole setup is portable
/// and shareable via version control.
///
/// File format:
/// <code>
/// {
///   "urls": ["https://api.example.com"],
///   "environments": [ ... ],
///   "globals": { ... },
///   "collections": [ ... ]
/// }
/// </code>
///
/// The file is read on startup and written back on every save. When
/// no workspace file exists, the endpoints return empty defaults.
/// </summary>
internal static class BowireWorkspaceEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static string WorkspacePath =>
        Path.Combine(Directory.GetCurrentDirectory(), ".blw");

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
        // CodeQL flagged the earlier per-platform Process.Start calls
        // as cs/command-line-injection because the path (derived from
        // a sanitised but request-bound workspaceId) was string-
        // interpolated into the arguments. The interpolation was the
        // attack surface, not the path itself — SanitiseWorkspaceId
        // already strips everything outside [A-Za-z0-9_-] and the
        // value lands under BowireUserContext.GetUserPath, so the
        // resolved path never escapes the user root. Removing the
        // arguments path entirely closes the static-analysis finding
        // without weakening the runtime guarantees: pass the directory
        // as ProcessStartInfo.FileName with UseShellExecute=true and
        // the OS opens its native file manager at that location
        // (Explorer on Windows, Finder on macOS, xdg-open on Linux).
        // No command line is constructed, so there is no string to
        // inject into.
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        };
        Process.Start(psi);
    }

    internal sealed record WorkspaceFile
    {
        public List<string> Urls { get; init; } = [];
        public List<JsonElement> Environments { get; init; } = [];
        public Dictionary<string, string> Globals { get; init; } = new();
        public List<JsonElement> Collections { get; init; } = [];
    }
}

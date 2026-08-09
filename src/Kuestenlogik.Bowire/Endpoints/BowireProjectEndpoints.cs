// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Read-only discovery endpoint for the <c>.bowire/project.json</c> convention
/// (#172). The workbench's Open-Folder / boot path calls
/// <c>GET {basePath}/api/project</c> so a repository that ships a checked-in
/// manifest surfaces its declared sources / suites / security automatically —
/// no re-configuration per checkout. The endpoint runs
/// <see cref="BowireProjectLoader.Discover"/> (a walk UP toward the filesystem
/// root) from the host's working directory, or from an explicit
/// <c>?path=</c> folder when the caller names one.
/// <para>
/// "No project" is the common case, not an error: the walk returns nothing and
/// the endpoint answers <c>404</c> with <c>{ "found": false }</c> so the JS
/// probe's <c>r.ok</c> gate degrades to a no-op exactly like the other
/// capability probes at boot. It never throws on not-found; a malformed
/// manifest that is present but unparseable answers <c>400</c> so the mistake
/// is visible rather than silently swallowed.
/// </para>
/// </summary>
internal static class BowireProjectEndpoints
{
    public static IEndpointRouteBuilder MapBowireProjectEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/project", (HttpContext ctx) =>
        {
            // Optional caller-supplied start folder. Discover already treats a
            // null/blank/unreadable/malformed path defensively (returns null),
            // so we don't need to pre-validate — an unusable path simply reads
            // as "no project here", matching the not-found contract.
            var startDir = ctx.Request.Query["path"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(startDir)) startDir = null;

            var location = BowireProjectLoader.Discover(startDir);
            if (location is null)
            {
                return Results.Json(new { found = false },
                    BowireEndpointHelpers.JsonOptions, statusCode: 404);
            }

            BowireProjectFile project;
            try
            {
                project = BowireProjectLoader.Load(location.FilePath);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Present but unparseable: surface the authoring mistake instead
                // of pretending nothing is there.
                return Results.Json(
                    new { found = true, error = "Invalid project file: " + ex.Message, filePath = location.FilePath },
                    BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }
            catch (IOException ex)
            {
                return Results.Json(
                    new { found = true, error = "Could not read project file: " + ex.Message, filePath = location.FilePath },
                    BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            // Non-fatal schema checks ride along so the workbench can warn
            // without the load itself failing (empty list when clean).
            var warnings = project.Validate();

            return Results.Json(new
            {
                found = true,
                filePath = location.FilePath,
                projectRoot = location.ProjectRoot,
                name = project.Name,
                sources = project.Sources,
                suites = project.Suites,
                security = project.Security,
                rules = project.Rules,
                warnings,
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}

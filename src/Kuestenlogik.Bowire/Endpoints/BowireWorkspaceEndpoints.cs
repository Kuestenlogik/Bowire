// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        this IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapGet($"/{prefix}/api/workspace", () =>
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

        endpoints.MapPut($"/{prefix}/api/workspace", async (HttpContext ctx) =>
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
                return Results.BadRequest(new { error = ex.Message });
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    internal sealed record WorkspaceFile
    {
        public List<string> Urls { get; init; } = [];
        public List<JsonElement> Environments { get; init; } = [];
        public Dictionary<string, string> Globals { get; init; } = new();
        public List<JsonElement> Collections { get; init; } = [];
    }
}

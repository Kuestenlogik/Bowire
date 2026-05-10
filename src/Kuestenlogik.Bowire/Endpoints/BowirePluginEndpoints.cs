// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Plugin marketplace endpoints — list installed plugins and search
/// NuGet for available ones. Installation runs the <c>dotnet tool</c>
/// flow server-side (same as <c>bowire plugin install</c> CLI).
///
/// These endpoints power the in-UI plugin browser so users can
/// discover and install protocol plugins without leaving Bowire.
/// </summary>
internal static class BowirePluginEndpoints
{
    private static readonly string PluginDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "plugins");

    public static IEndpointRouteBuilder MapBowirePluginEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        // List installed plugins
        endpoints.MapGet($"{basePath}/api/plugins", () =>
        {
            var plugins = new List<object>();
            if (!Directory.Exists(PluginDir))
                return Results.Ok(new { plugins });

            foreach (var dir in Directory.GetDirectories(PluginDir))
            {
                var metaPath = Path.Combine(dir, "plugin.json");
                if (File.Exists(metaPath))
                {
                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        var meta = JsonSerializer.Deserialize<JsonElement>(json);
                        plugins.Add(meta);
                    }
                    catch { /* skip broken */ }
                }
                else
                {
                    plugins.Add(new
                    {
                        packageId = Path.GetFileName(dir),
                        version = "unknown",
                        files = Directory.GetFiles(dir, "*.dll").Length
                    });
                }
            }
            return Results.Ok(new { plugins });
        }).ExcludeFromDescription();

        // Search NuGet for Bowire plugins
        endpoints.MapGet($"{basePath}/api/plugins/search", async (HttpContext ctx) =>
        {
            var query = ctx.Request.Query["q"].ToString();
            // Default search: look for the bowire-plugin tag so both
            // official and third-party plugins are discoverable. Users
            // can override with a custom query string.
            if (string.IsNullOrWhiteSpace(query)) query = "tags:bowire-plugin";

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                // NuGet v3 search supports packageType filter — use it
                // when available so only BowirePlugin-typed packages
                // appear. Falls back to tag-based search otherwise.
                var nugetUrl = $"https://api-v2v3search-0.nuget.org/query?q={Uri.EscapeDataString(query)}&take=20&prerelease=false&packageType=BowirePlugin";
                var resp = await http.GetStringAsync(new Uri(nugetUrl));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(resp);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }).ExcludeFromDescription();

        // List the protocol ids currently registered in the
        // BowireProtocolRegistry — what the in-process replay path
        // can actually dispatch to. Distinct from /api/plugins (which
        // walks the plugins dir): a package can be installed but not
        // loaded if a previous restart picked it up. The recording
        // manager diffs the recording's protocols against this list
        // so it can pop a "missing plugins" modal before replay starts.
        // The catalog map lets the modal offer an install command for
        // every protocol Bowire knows about without duplicating the
        // hardcoded protocol→package mapping on the JS side.
        endpoints.MapGet($"{basePath}/api/plugins/protocols", () =>
        {
            var registry = BowireEndpointHelpers.GetRegistry();
            var loaded = registry.Protocols
                .Select(p => p.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var catalog = PluginLoading.PluginPackageMap.Snapshot();
            return Results.Ok(new { loaded, catalog });
        }).ExcludeFromDescription();

        // Install a plugin (runs dotnet nuget download + extract)

        endpoints.MapPost($"{basePath}/api/plugins/install", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var req = JsonSerializer.Deserialize<JsonElement>(body);
            var packageId = req.TryGetProperty("packageId", out var pid) ? pid.GetString() : null;
            var version = req.TryGetProperty("version", out var ver) ? ver.GetString() : null;

            if (string.IsNullOrWhiteSpace(packageId))
                return Results.BadRequest(new { error = "packageId required" });

            // Shell out to 'bowire plugin install' for safety — reuses
            // the existing NuGet download + extract logic. In a future
            // iteration this could call PluginManager directly.
            try
            {
                var args = $"plugin install {packageId}";
                if (!string.IsNullOrEmpty(version)) args += $" --version {version}";

                var psi = new ProcessStartInfo("bowire", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return Results.StatusCode(500);

                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                return proc.ExitCode == 0
                    ? Results.Ok(new { installed = true, packageId, output })
                    : Results.StatusCode(500);
            }
            catch
            {
                return Results.StatusCode(500);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }
}

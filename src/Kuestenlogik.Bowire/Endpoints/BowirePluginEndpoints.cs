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

        // Plugin load health — surfaces the PluginLoadResult set the
        // loader produced at startup. Operators looking at "0 services"
        // can hit this endpoint to see WHY a sibling plugin failed
        // (contract major mismatch after a tool update, manifest
        // missing, etc.) instead of debugging in the dark. Empty
        // array before the first LoadPlugins call (no plugin dir
        // configured / no plugins installed).
        endpoints.MapGet($"{basePath}/api/plugins/health", () =>
        {
            var latest = PluginLoading.PluginLoadResultStore.Latest;
            var entries = latest.Select(r => new
            {
                packageId = r.PackageId,
                directory = r.DirectoryPath,
                status = r.Status.ToString(),
                errorMessage = r.ErrorMessage,
            }).ToArray();
            return Results.Ok(new { plugins = entries });
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
            var prerelease = req.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(packageId))
                return Results.BadRequest(new { error = "packageId required" });

            return await RunBowirePluginCommandAsync("install", packageId, version, prerelease);
        }).ExcludeFromDescription();

        // Update a single installed plugin (or all when packageId == "all")
        endpoints.MapPost($"{basePath}/api/plugins/{{packageId}}/update", async (string packageId, HttpContext ctx) =>
        {
            string? version = null;
            var prerelease = false;
            if (ctx.Request.ContentLength is > 0)
            {
                var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var req = JsonSerializer.Deserialize<JsonElement>(body);
                    if (req.TryGetProperty("version", out var ver)) version = ver.GetString();
                    if (req.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                        prerelease = true;
                }
            }

            // packageId "all" → bowire plugin update (no id, updates everything).
            // The CLI's update command treats omitted id as 'update all'.
            var idForCli = string.Equals(packageId, "all", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : packageId;
            return await RunBowirePluginCommandAsync("update", idForCli, version, prerelease);
        }).ExcludeFromDescription();

        // Uninstall an installed plugin
        endpoints.MapDelete($"{basePath}/api/plugins/{{packageId}}", async (string packageId) =>
        {
            if (string.IsNullOrWhiteSpace(packageId))
                return Results.BadRequest(new { error = "packageId required" });
            return await RunBowirePluginCommandAsync("uninstall", packageId, version: null, prerelease: false);
        }).ExcludeFromDescription();

        // Look up the latest version of a plugin on the configured feed.
        // Used by the UI's "update available" hint — compared client-side
        // against the installed version returned by GET /api/plugins.
        endpoints.MapGet($"{basePath}/api/plugins/{{packageId}}/latest", async (string packageId, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(packageId))
                return Results.BadRequest(new { error = "packageId required" });

            var prerelease = ctx.Request.Query["prerelease"].ToString() == "true";
            // NuGet v3 registration index — same source the package
            // manager uses. Stable resolution: pick the latest version
            // (or the latest prerelease when the flag is on). Stays
            // dependency-free — no NuGet.Protocol pull into the host.
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                // NuGet's v3-flatcontainer requires lowercase package
                // ids — the CA1308 'use ToUpperInvariant' guidance
                // doesn't apply to URL path segments.
#pragma warning disable CA1308
                var idLower = packageId.ToLowerInvariant();
#pragma warning restore CA1308
                var indexUrl = $"https://api.nuget.org/v3-flatcontainer/{idLower}/index.json";
                var resp = await http.GetStringAsync(new Uri(indexUrl));
                using var doc = JsonDocument.Parse(resp);
                var versions = doc.RootElement.GetProperty("versions").EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToList();
                string? latest;
                if (prerelease)
                {
                    latest = versions.LastOrDefault();
                }
                else
                {
                    latest = versions.LastOrDefault(v =>
                        v is not null && !v.Contains('-', StringComparison.Ordinal));
                }
                return latest is null
                    ? Results.NotFound(new { packageId, error = "no versions found" })
                    : Results.Ok(new { packageId, latest, prerelease });
            }
            catch (Exception ex)
            {
                return Results.Json(new { packageId, error = ex.Message }, statusCode: 502);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Shared shell-out wrapper for the plugin-mutation endpoints
    /// (install / update / uninstall). All three call the in-PATH
    /// 'bowire' CLI so the host doesn't have to re-implement the
    /// PluginManager flow over HTTP. Returns the CLI's exit code +
    /// stdout for the caller to surface to the user.
    /// </summary>
    private static async Task<IResult> RunBowirePluginCommandAsync(
        string verb, string packageIdOrEmpty, string? version, bool prerelease)
    {
        try
        {
            var args = $"plugin {verb}";
            if (!string.IsNullOrEmpty(packageIdOrEmpty))
                args += $" {packageIdOrEmpty}";
            if (!string.IsNullOrEmpty(version))
                args += $" --version {version}";
            if (prerelease && (verb == "install" || verb == "update"))
                args += " --prerelease";

            var psi = new ProcessStartInfo("bowire", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Results.StatusCode(500);

            var output = await proc.StandardOutput.ReadToEndAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? Results.Ok(new { ok = true, verb, packageId = packageIdOrEmpty, output })
                : Results.Json(
                    new { ok = false, verb, packageId = packageIdOrEmpty, exitCode = proc.ExitCode, output, error = err },
                    statusCode: 500);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, verb, error = ex.Message }, statusCode: 500);
        }
    }
}

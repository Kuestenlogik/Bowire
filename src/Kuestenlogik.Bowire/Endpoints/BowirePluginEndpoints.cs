// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        // List every plugin Bowire knows about — sibling-installed
        // (writable under ~/.bowire/plugins/) and bundled (shipped
        // inside the bowire tool itself, read-only). Each entry
        // carries `source: "sibling" | "bundled"` so the UI can
        // surface both in one list but disable the lifecycle buttons
        // on the bundled half — they're only updated by re-running
        // `dotnet tool update -g Kuestenlogik.Bowire.Tool` (or the
        // package-manager equivalent).
        endpoints.MapGet($"{basePath}/api/plugins", () =>
        {
            var plugins = new List<object>();

            // #167 — Build an assembly-name → IBowireProtocol[] map up
            // front so both the sibling and bundled paths can enrich
            // every plugin row with the protocol's user-facing
            // DisplayName + Description without re-walking the
            // registry per row.
            var registry = BowireEndpointHelpers.GetRegistry();
            var byAssembly = registry.Protocols
                .Where(p => p is not null)
                .GroupBy(p => p.GetType().Assembly.GetName().Name ?? "",
                    StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            static (string displayName, string description) DescribeAssembly(
                string packageId, IReadOnlyDictionary<string, List<IBowireProtocol>> map)
            {
                if (!map.TryGetValue(packageId, out var list) || list.Count == 0)
                    return ("", "");
                var names = list.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToArray();
                var displayName = string.Join(" / ", names);
                var description = list.Select(p => p.Description ?? "").FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";
                return (displayName, description);
            }

            // ---- Sibling plugins (writable, lifecycle-acted-on) ----
            // Same plugin-dir walk the original endpoint did, but each
            // entry gets source="sibling" so the merged view can
            // distinguish them. plugin.json may carry displayName /
            // description fields directly — when present they win;
            // otherwise we fall back to the in-process registry lookup
            // by assembly name (same as bundled plugins) so first-party
            // ones with a published manifest still get nice labels.
            if (Directory.Exists(PluginDir))
            {
                foreach (var dir in Directory.GetDirectories(PluginDir))
                {
                    var metaPath = Path.Combine(dir, "plugin.json");
                    string siblingPackageId;
                    Dictionary<string, object?> dict;
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(metaPath);
                            var meta = JsonSerializer.Deserialize<JsonElement>(json);
                            dict = meta.EnumerateObject()
                                .ToDictionary(p => p.Name, p => (object?)p.Value);
                        }
                        catch
                        {
                            continue; /* skip broken */
                        }
                        siblingPackageId = dict.TryGetValue("packageId", out var sidVal) && sidVal is JsonElement se
                            ? se.GetString() ?? Path.GetFileName(dir)
                            : Path.GetFileName(dir);
                    }
                    else
                    {
                        siblingPackageId = Path.GetFileName(dir);
                        dict = new Dictionary<string, object?>
                        {
                            ["packageId"] = siblingPackageId,
                            ["version"] = "unknown",
                            ["files"] = Directory.GetFiles(dir, "*.dll").Length,
                        };
                    }
                    static bool IsMissingOrEmptyString(IDictionary<string, object?> d, string key)
                    {
                        if (!d.TryGetValue(key, out var v) || v is null) return true;
                        if (v is JsonElement je && je.ValueKind == JsonValueKind.String)
                            return string.IsNullOrEmpty(je.GetString());
                        if (v is JsonElement js && js.ValueKind == JsonValueKind.Null) return true;
                        return v is string sv && string.IsNullOrEmpty(sv);
                    }
                    if (IsMissingOrEmptyString(dict, "displayName"))
                    {
                        var (dn, _) = DescribeAssembly(siblingPackageId, byAssembly);
                        if (!string.IsNullOrEmpty(dn)) dict["displayName"] = dn;
                    }
                    if (IsMissingOrEmptyString(dict, "description"))
                    {
                        var (_, de) = DescribeAssembly(siblingPackageId, byAssembly);
                        if (!string.IsNullOrEmpty(de)) dict["description"] = de;
                    }
                    dict["source"] = "sibling";
                    plugins.Add(dict);
                }
            }

            // ---- Bundled plugins (read-only, shipped in the tool) ----
            // Walk the loaded AppDomain assemblies for anything named
            // Kuestenlogik.Bowire.Protocol.* / Extension.* — those are
            // the in-process plugins the host carries. Version comes
            // from AssemblyInformationalVersion (set by the tool's
            // release pipeline); falls back to AssemblyVersion.
            var siblingIds = new HashSet<string>(
                plugins.Select(p =>
                {
                    if (p is IDictionary<string, object?> d &&
                        d.TryGetValue("packageId", out var v))
                    {
                        return v?.ToString() ?? "";
                    }
                    return "";
                }).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!IsBowirePluginAssemblyName(name)) continue;
                if (siblingIds.Contains(name)) continue; // sibling overrides bundled if both present

                var infoVersion = asm
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()
                    ?.InformationalVersion;
                var version = infoVersion ?? asm.GetName().Version?.ToString() ?? "unknown";
                // AssemblyInformationalVersion sometimes carries SourceLink build
                // metadata (e.g. "1.5.1+abc1234"); strip after '+' for display.
                var plus = version.IndexOf('+', StringComparison.Ordinal);
                if (plus > 0) version = version[..plus];

                var (displayName, description) = DescribeAssembly(name, byAssembly);
                plugins.Add(new
                {
                    packageId = name,
                    displayName,
                    description,
                    version,
                    source = "bundled",
                });
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
                ctx.Response.ContentType = "application/problem+json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "urn:bowire:plugin:search-failed",
                    title = "Plugin feed search failed",
                    status = 502,
                    detail = ex.Message,
                    instance = "/api/plugins/search"
                }));
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
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var req = JsonSerializer.Deserialize<JsonElement>(body);
            var packageId = req.TryGetProperty("packageId", out var pid) ? pid.GetString() : null;
            var version = req.TryGetProperty("version", out var ver) ? ver.GetString() : null;
            var prerelease = req.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(packageId))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "packageId is required",
                    status: 400,
                    instance: ctx.Request.Path);

            return await RunBowirePluginCommandAsync("install", packageId, version, prerelease);
        }).ExcludeFromDescription();

        // Update a single installed plugin (or all when packageId == "all")
        endpoints.MapPost($"{basePath}/api/plugins/{{packageId}}/update", async (string packageId, HttpContext ctx) =>
        {
            string? version = null;
            var prerelease = false;
            if (ctx.Request.ContentLength is > 0)
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ctx.RequestAborted);
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
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "packageId is required",
                    status: 400,
                    instance: "/api/plugins/{packageId}");
            return await RunBowirePluginCommandAsync("uninstall", packageId, version: null, prerelease: false);
        }).ExcludeFromDescription();

        // Look up the latest version of a plugin on the configured feed.
        // Used by the UI's "update available" hint — compared client-side
        // against the installed version returned by GET /api/plugins.
        endpoints.MapGet($"{basePath}/api/plugins/{{packageId}}/latest", async (string packageId, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(packageId))
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "packageId is required",
                    status: 400,
                    instance: ctx.Request.Path);

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
                    ? BowireEndpointHelpers.Problem(
                        type: "urn:bowire:plugin:no-versions",
                        title: $"No versions found for {packageId}",
                        status: 404,
                        instance: ctx.Request.Path,
                        extensions: new Dictionary<string, object?> { ["packageId"] = packageId })
                    : Results.Ok(new { packageId, latest, prerelease });
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:plugin:registry-error",
                    title: $"Couldn't reach the plugin registry for {packageId}",
                    status: 502,
                    detail: ex.Message,
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> {
                        ["packageId"] = packageId,
                        ["exceptionType"] = ex.GetType().Name,
                    });
            }
        }).ExcludeFromDescription();

        // Manual update check: hit nuget.org for every installed
        // sibling plugin's latest version. Powers the "Check now"
        // button in the plugin manage panel. Always available (it's
        // a direct user action) — the opt-in flag only gates the
        // *background* check, not on-demand requests.
        endpoints.MapGet($"{basePath}/api/plugins/check-updates", async (HttpContext ctx) =>
        {
            var prerelease = ctx.Request.Query["prerelease"].ToString() == "true";
            var svc = ctx.RequestServices.GetRequiredService<PluginUpdateCheckService>();
            try
            {
                var snapshot = await svc.CheckAsync(prerelease, ctx.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(snapshot);
            }
            catch (Exception ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:plugin:check-failed",
                    title: "Plugin update check failed",
                    status: 502,
                    detail: ex.Message,
                    instance: ctx.Request.Path,
                    extensions: new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
            }
        }).ExcludeFromDescription();

        // Read the last persisted snapshot + whether the background
        // checker is enabled. Used by the sidebar badge to render
        // "N updates available" without hitting the network on every
        // page load. Returns 200 with `cached=null` when no check has
        // run yet (background disabled + manual button never pressed).
        endpoints.MapGet($"{basePath}/api/plugins/check-updates/status", (HttpContext ctx) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptions<BowirePluginUpdateCheckOptions>>();
            var cfg = opts.Value;
            return Results.Ok(new
            {
                enabled = cfg.Enabled,
                intervalHours = cfg.IntervalHours,
                includePrerelease = cfg.IncludePrerelease,
                cached = PluginUpdateCheckService.ReadCached(),
            });
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
    /// <summary>
    /// Whitelist for plugin / version identifiers — alphanumerics
    /// plus dots, dashes, underscores, plus '+' for SemVer build
    /// metadata. Anything else (shell metacharacters, slashes,
    /// spaces) trips the input-validation guard. Matches the
    /// character set NuGet itself accepts in package ids and SemVer.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SafeIdentifier =
        new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task<IResult> RunBowirePluginCommandAsync(
        string verb, string packageIdOrEmpty, string? version, bool prerelease)
    {
        // Input validation — packageId / version come straight off the
        // HTTP request body. Refuse anything that isn't NuGet-shape so
        // shell metacharacters, path separators, --flags etc. can't
        // sneak into the child process's argument list. The
        // ArgumentList approach below already prevents shell parsing,
        // but the whitelist is a defence-in-depth against the case
        // where the input would otherwise reach the bowire CLI's own
        // argument parser as a malformed value.
        if (!string.IsNullOrEmpty(packageIdOrEmpty) && !SafeIdentifier.IsMatch(packageIdOrEmpty))
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:plugin:invalid-id",
                title: "Invalid packageId",
                status: 400,
                detail: "Plugin ids must match [A-Za-z0-9][A-Za-z0-9._+-]* — alphanumerics plus dots / dashes / underscores / +. Anything else trips the input-validation guard.",
                instance: "/api/plugins",
                extensions: new Dictionary<string, object?> { ["packageId"] = packageIdOrEmpty });
        }
        if (!string.IsNullOrEmpty(version) && !SafeIdentifier.IsMatch(version))
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:plugin:invalid-version",
                title: "Invalid version",
                status: 400,
                detail: "Versions must match [A-Za-z0-9][A-Za-z0-9._+-]* (NuGet / SemVer character set).",
                instance: "/api/plugins",
                extensions: new Dictionary<string, object?> { ["version"] = version });
        }

        try
        {
            var psi = new ProcessStartInfo("bowire")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // ArgumentList builds the child-process argv directly —
            // each element becomes one argv slot, no shell parsing,
            // no interpolation of metacharacters from the operator
            // input into a single command string.
            psi.ArgumentList.Add("plugin");
            psi.ArgumentList.Add(verb);
            if (!string.IsNullOrEmpty(packageIdOrEmpty))
                psi.ArgumentList.Add(packageIdOrEmpty);
            if (!string.IsNullOrEmpty(version))
            {
                psi.ArgumentList.Add("--version");
                psi.ArgumentList.Add(version);
            }
            if (prerelease && (verb == "install" || verb == "update"))
                psi.ArgumentList.Add("--prerelease");

            using var proc = Process.Start(psi);
            if (proc is null) return Results.StatusCode(500);

            var output = await proc.StandardOutput.ReadToEndAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? Results.Ok(new { ok = true, verb, packageId = packageIdOrEmpty, output })
                : BowireEndpointHelpers.Problem(
                    type: "urn:bowire:plugin:cli-failed",
                    title: $"bowire plugin {verb} failed (exit {proc.ExitCode})",
                    status: 500,
                    detail: string.IsNullOrEmpty(err) ? output : err,
                    instance: "/api/plugins",
                    extensions: new Dictionary<string, object?> {
                        ["verb"] = verb,
                        ["packageId"] = packageIdOrEmpty,
                        ["exitCode"] = proc.ExitCode,
                        ["stdout"] = output,
                    });
        }
        catch (Exception ex)
        {
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:plugin:cli-error",
                title: $"Couldn't run `bowire plugin {verb}`",
                status: 500,
                detail: ex.Message,
                instance: "/api/plugins",
                extensions: new Dictionary<string, object?> {
                    ["verb"] = verb,
                    ["exceptionType"] = ex.GetType().Name,
                });
        }
    }

    /// <summary>
    /// True for assembly names that correspond to first-party Bowire
    /// plugins (protocols + UI extensions + asyncapi). Walks the
    /// dotted-name prefix only — the IsHostProvided runtime check in
    /// NuGetPackageInstaller does the deeper work; here we just need
    /// a fast filter to populate the bundled half of /api/plugins.
    /// </summary>
    private static bool IsBowirePluginAssemblyName(string assemblyName) =>
        assemblyName.StartsWith("Kuestenlogik.Bowire.Protocol.", StringComparison.OrdinalIgnoreCase) ||
        // Legacy .Extension.* prefix kept for 3rd-party / pre-v2.0
        // packages (e.g. Extension.MapLibre 1.3.0-rc.1) until they
        // re-publish under the simpler name.
        assemblyName.StartsWith("Kuestenlogik.Bowire.Extension.", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.AsyncApi", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Mcp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Mock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Security.Scanner", StringComparison.OrdinalIgnoreCase) ||
        // First-party optional packages without the .Extension. prefix
        // (Ai / Help / Telemetry / Map). All ship as separate NuGets the
        // embedded host pulls in explicitly.
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Map", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Ai", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Telemetry", StringComparison.OrdinalIgnoreCase) ||
        // Per-version OpenAPI adapter packages — the REST plugin's
        // Microsoft.OpenApi seam (IBowireOpenApiAdapter). Embedded hosts
        // pick the line that matches their app's other OpenAPI
        // consumers; standalone CLI bundles one of these transitively.
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Protocol.Rest.OpenApi2", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assemblyName, "Kuestenlogik.Bowire.Protocol.Rest.OpenApi3", StringComparison.OrdinalIgnoreCase);
}

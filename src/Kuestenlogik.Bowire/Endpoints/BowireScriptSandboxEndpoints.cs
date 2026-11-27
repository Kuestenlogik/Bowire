// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// #1062 — the files the workbench runs pre-request and post-response
/// scripts with: QuickJS (vendored by <c>scripts/vendor/quickjs.mjs</c>) and
/// the script runtime it runs, <c>wwwroot/js/scripts.js</c>, as text.
/// </summary>
/// <remarks>
/// The page's policy refuses <c>'unsafe-eval'</c>, so a script never runs in
/// the page: the workbench loads these on demand and runs each script in a
/// QuickJS context of its own. The set is fixed; nothing else is served from
/// here. The files only change with the package, so they are cached for as
/// long as the page that asks for them carries the version in the URL.
/// </remarks>
internal static class BowireScriptSandboxEndpoints
{
    private static readonly FrozenDictionary<string, (string Resource, string ContentType)> s_files =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["quickjs.js"] = ("Kuestenlogik.Bowire.wwwroot.vendor.quickjs.quickjs.js", "text/javascript; charset=utf-8"),
            ["quickjs.wasm"] = ("Kuestenlogik.Bowire.wwwroot.vendor.quickjs.quickjs.wasm", "application/wasm"),
            ["LICENSE"] = ("Kuestenlogik.Bowire.wwwroot.vendor.quickjs.LICENSE", "text/plain; charset=utf-8"),
            ["scripts.js"] = ("Kuestenlogik.Bowire.wwwroot.js.scripts.js", "text/javascript; charset=utf-8"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapBowireScriptSandboxEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/ui/script-sandbox/{{name}}", (string name, HttpContext ctx) =>
        {
            if (!s_files.TryGetValue(name, out var file)) return Results.NotFound();
            using var stream = typeof(BowireScriptSandboxEndpoints).Assembly.GetManifestResourceStream(file.Resource);
            if (stream is null) return Results.NotFound();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            if (ctx.Request.Query.ContainsKey("v"))
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(ms.ToArray(), file.ContentType);
        }).ExcludeFromDescription();

        return endpoints;
    }
}

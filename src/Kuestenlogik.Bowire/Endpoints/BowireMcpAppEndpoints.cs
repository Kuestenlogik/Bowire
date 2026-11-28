// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>The domains an MCP App's resource asks for (<c>_meta.ui.csp</c>, #810).</summary>
public sealed record McpAppCsp(
    [property: JsonPropertyName("connectDomains")] IReadOnlyList<string>? ConnectDomains = null,
    [property: JsonPropertyName("resourceDomains")] IReadOnlyList<string>? ResourceDomains = null,
    [property: JsonPropertyName("frameDomains")] IReadOnlyList<string>? FrameDomains = null,
    [property: JsonPropertyName("baseUriDomains")] IReadOnlyList<string>? BaseUriDomains = null);

/// <summary>An MCP App's document, to be hosted.</summary>
public sealed record McpAppViewRequest(
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("csp")] McpAppCsp? Csp = null);

/// <summary>
/// #810 — where the workbench hosts an MCP App: the <c>ui://</c> resource's
/// HTML served from a URL of its own, under the policy its <c>_meta.ui.csp</c>
/// asks for, in a sandbox.
/// </summary>
/// <remarks>
/// <para>
/// A document the page builds itself (<c>srcdoc</c>, <c>blob:</c>) inherits
/// the workbench's policy, which refuses an app's inline script; so the
/// workbench reads the resource the way it reads any (one invoke, the same
/// rights and audit), hands its HTML and CSP here, and frames the URL it gets
/// back.
/// </para>
/// <para>
/// The answer carries <c>sandbox allow-scripts allow-forms</c> in its own
/// policy: opened in a tab of its own, the app still runs in an opaque origin,
/// with no access to the workbench's cookies or storage. Its network reach is
/// exactly the domains it declared — none when it declared none.
/// </para>
/// <para>
/// Views live in memory for an hour, at most <see cref="MaxViews"/> of them,
/// the oldest going first; they are not shared between hosts.
/// </para>
/// </remarks>
public static class BowireMcpAppEndpoints
{
    /// <summary>Most views kept at once.</summary>
    public const int MaxViews = 128;

    /// <summary>Largest document accepted, in characters.</summary>
    public const int MaxHtmlChars = 5 * 1024 * 1024;

    private static readonly string[] s_htmlRequired = ["html is required"];
    private static readonly TimeSpan s_lifetime = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (string Html, string Policy, DateTimeOffset Created)> s_views = new(StringComparer.Ordinal);

    // An origin as the spec writes them: scheme://host[:port], a leading *.
    // allowed in the host. Anything else (quotes, ;, keywords) is dropped, so
    // a resource cannot write its own directives into the policy.
    private static readonly Regex s_origin = new(@"^(https?|wss?)://(\*\.)?[A-Za-z0-9.-]+(:\d{1,5})?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>Maps the view store and the vendored bridge.</summary>
    public static IEndpointRouteBuilder MapBowireMcpAppEndpoints(this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/ui/mcp-apps/views", (McpAppViewRequest? request) =>
        {
            if (string.IsNullOrEmpty(request?.Html)) return Results.BadRequest(new { errors = s_htmlRequired });
            if (request.Html.Length > MaxHtmlChars) return Results.BadRequest(new { errors = new[] { $"the document is larger than {MaxHtmlChars} characters" } });
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Prune();
            s_views[id] = (request.Html, PolicyFor(request.Csp), DateTimeOffset.UtcNow);
            return Results.Ok(new { id, url = $"{basePath}/api/ui/mcp-apps/views/{id}" });
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/ui/mcp-apps/views/{{id}}", (string id, HttpContext ctx) =>
        {
            if (!s_views.TryGetValue(id, out var view) || DateTimeOffset.UtcNow - view.Created > s_lifetime)
                return Results.NotFound();
            ctx.Response.Headers["Content-Security-Policy"] = view.Policy;
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Content(view.Html, "text/html; charset=utf-8", Encoding.UTF8);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/ui/mcp-apps/app-bridge.js", (HttpContext ctx) =>
        {
            using var stream = typeof(BowireMcpAppEndpoints).Assembly.GetManifestResourceStream("Kuestenlogik.Bowire.wwwroot.vendor.mcp-apps.mcp-app-bridge.js");
            if (stream is null) return Results.NotFound();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            if (ctx.Request.Query.ContainsKey("v")) ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(ms.ToArray(), "text/javascript; charset=utf-8");
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>The policy an app runs under: what it declared, nothing more.</summary>
    internal static string PolicyFor(McpAppCsp? csp)
    {
        static string Sources(IReadOnlyList<string>? domains, string fallback)
        {
            var valid = (domains ?? []).Where(d => d is not null && s_origin.IsMatch(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return valid.Count > 0 ? string.Join(' ', valid) : fallback;
        }

        var resources = Sources(csp?.ResourceDomains, "");
        string With(string own) => string.IsNullOrEmpty(resources) ? own : own + " " + resources;
        return string.Join("; ",
            "sandbox allow-scripts allow-forms",
            "default-src 'none'",
            With("script-src 'unsafe-inline'"),
            With("style-src 'unsafe-inline'"),
            With("img-src data: blob:"),
            With("font-src data:"),
            With("media-src data: blob:"),
            "connect-src " + Sources(csp?.ConnectDomains, "'none'"),
            "frame-src " + Sources(csp?.FrameDomains, "'none'"),
            "base-uri " + Sources(csp?.BaseUriDomains, "'none'"),
            "form-action 'none'",
            "frame-ancestors 'self'");
    }

    private static void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, view) in s_views)
        {
            if (now - view.Created > s_lifetime) s_views.TryRemove(key, out _);
        }
        while (s_views.Count >= MaxViews)
        {
            var oldest = s_views.OrderBy(v => v.Value.Created).FirstOrDefault();
            if (oldest.Key is null || !s_views.TryRemove(oldest.Key, out _)) break;
        }
    }
}

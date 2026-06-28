// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Generates the Bowire HTML page with inlined CSS and JS.
/// Like Scalar, all assets are embedded — no static file serving needed.
/// </summary>
internal static class BowireHtmlGenerator
{
    private static readonly Lazy<string> CssContent = new(() => ReadEmbeddedFile("bowire.css"));
    private static readonly Lazy<string> JsContent = new(() => StitchRailFragments(ReadEmbeddedFile("bowire.js")));

    // #294 — Rail + module catalogues populated by BowireApiEndpoints.Map
    // at host startup. Defaulting to empty registries means a test or
    // call path that bypasses the endpoint mapping still gets a sane
    // (empty) catalogue rather than NRE-on-first-access — the JS bundle
    // is also defensive about an empty list.
    private static BowireRailRegistry _rails = new();
    private static BowireModuleRegistry _modules = new();

    /// <summary>
    /// Called once at host startup from <see cref="BowireApiEndpoints.Map"/>
    /// so the HTML config can render the seeded rail + module
    /// catalogues. The setters are static + last-writer-wins because
    /// the catalogues are fixed for the lifetime of the process
    /// (descriptors aren't hot-reloaded today).
    /// </summary>
    internal static void SetContributions(
        BowireRailRegistry rails, BowireModuleRegistry modules)
    {
        _rails = rails ?? new BowireRailRegistry();
        _modules = modules ?? new BowireModuleRegistry();
    }

    /// <summary>
    /// Bowire favicon — three derivatives from the same source SVG,
    /// pre-rendered as base64 data URLs once at first access so the HTML
    /// can inline them without extra HTTP roundtrips.
    ///
    /// The source SVG (wwwroot/favicon.svg) carries (a) inline
    /// `fill:#000000` style attributes on each path so it renders black
    /// in any context that ignores CSS, and (b) an embedded
    /// `&lt;style&gt;@media(prefers-color-scheme:dark){…!important;}&lt;/style&gt;`
    /// block that flips the fill to white when the OS reports dark mode.
    /// `!important` is required to beat the inline styles.
    ///
    /// Three variants because the workbench has its own theme switcher
    /// that can disagree with the OS preference (user forces app=light
    /// while OS=dark, or vice versa). For those cases the OS-driven
    /// media query would render the wrong colour, so we strip it from
    /// the in-app variants:
    ///
    ///   FaviconBrowserTabDataUrl — source as-is. Used by
    ///     `&lt;link rel="icon"&gt;` so the browser tab respects OS theme.
    ///   FaviconDataUrl — media-query block stripped. Guaranteed black
    ///     regardless of OS theme. Used by the in-app loading-screen
    ///     `&lt;img&gt;` that displays when app theme is light.
    ///   FaviconMonoDataUrl — media-query block stripped AND inline
    ///     fills flipped to white. Guaranteed white regardless of OS
    ///     theme. Used by the in-app loading-screen `&lt;img&gt;` that
    ///     displays when app theme is dark.
    /// </summary>
    // FaviconBrowserTabDataUrl was the single-icon variant that relied
    // on the inline @media (prefers-color-scheme) inside the SVG to
    // flip black ↔ white. Chromium-family browsers don't reliably
    // honour CSS-inside-SVG-favicons; switched to two explicit
    // <link rel="icon"> tags with media="(prefers-color-scheme: ...)"
    // attributes that point at the deterministic black + white
    // variants below — every browser respects the attribute-level
    // media query.

    private static readonly Lazy<string> FaviconDataUrl = new(() =>
    {
        var svg = StripPrefersColorSchemeStyle(ReadEmbeddedFile("favicon.svg"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
    });

    private static readonly Lazy<string> FaviconMonoDataUrl = new(() =>
    {
        // Strip the OS-theme media query first, then flip every black
        // fill to white. Order matters: leaving the media query in
        // would let the browser undo our flip on light-OS hosts.
        var svg = StripPrefersColorSchemeStyle(ReadEmbeddedFile("favicon.svg"));
        svg = System.Text.RegularExpressions.Regex.Replace(
            svg,
            @"fill:\s*#000000|fill:\s*#000\b|fill:\s*black|fill=""#000000""|fill=""#000""|fill=""black""",
            m =>
            {
                var s = m.Value;
                if (s.StartsWith("fill=\"", StringComparison.Ordinal)) return "fill=\"#ffffff\"";
                return "fill:#ffffff";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
    });

    /// <summary>
    /// Removes the prefers-color-scheme &lt;style&gt; block from a Bowire
    /// SVG so the resulting variant renders deterministically regardless
    /// of OS theme. Targets the specific `path { fill: #ffffff !important }`
    /// rule we embed; leaves any other inline CSS untouched.
    /// </summary>
    private static string StripPrefersColorSchemeStyle(string svg)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            svg,
            @"<style[^>]*>\s*@media\s*\(prefers-color-scheme:\s*dark\)\s*\{[^}]*\{[^}]*\}\s*\}\s*</style>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    /// <summary>
    /// Küstenlogik horizontal lockup, base64-encoded once at first access.
    /// Surfaced into the JS UI as a brand asset for the About panel
    /// (settings.js → renderSettingsAbout). The source SVG ships with
    /// black ink and stroke; we serve it as-is for light theme and emit a
    /// white-flipped twin for dark theme via the same mechanism the
    /// favicon uses, extended to also flip stroke="..." attributes (the
    /// lockup uses outlined hook glyphs that the favicon doesn't have).
    /// </summary>
    private static readonly Lazy<string> KuestenlogikLockupDataUrl = new(() =>
    {
        var svg = ReadEmbeddedFile("kuestenlogik_lockup.svg");
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
    });

    private static readonly Lazy<string> KuestenlogikLockupMonoDataUrl = new(() =>
    {
        var svg = ReadEmbeddedFile("kuestenlogik_lockup.svg");
        svg = System.Text.RegularExpressions.Regex.Replace(
            svg,
            @"(fill|stroke):\s*#000000|(fill|stroke):\s*#000\b|(fill|stroke):\s*black|(fill|stroke)=""#000000""|(fill|stroke)=""#000""|(fill|stroke)=""black""",
            m =>
            {
                var s = m.Value;
                var attr = s.StartsWith("fill", StringComparison.OrdinalIgnoreCase) ? "fill" : "stroke";
                if (s.Contains('=')) return attr + "=\"#ffffff\"";
                return attr + ":#ffffff";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
    });

    /// <summary>
    /// Assembly informational version (e.g. "0.9.4-rc.6") so the About
    /// panel always displays the version of the .dll the user actually
    /// runs, not a hardcoded constant in the JS that gets stale across
    /// every release. Falls back to the assembly file version for
    /// unusual builds where InformationalVersion isn't set.
    /// </summary>
    private static readonly Lazy<string> AssemblyVersionString = new(() =>
    {
        var assembly = typeof(BowireHtmlGenerator).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // SourceLink appends "+<commit-sha>" to InformationalVersion;
            // strip it for the user-facing About display.
            var plus = info!.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString() ?? "unknown";
    });

    public static string GenerateIndexHtml(BowireOptions options, HttpRequest request)
    {
        // Same shape as BowireApiEndpoints.Map: an empty RoutePrefix means
        // the workbench is mounted at site root, so the JS-side `prefix`
        // must collapse to "" — otherwise `fetch(`${config.prefix}/api/…`)`
        // sees `//api/…` and 404s.
        var trimmedPrefix = options.RoutePrefix.TrimStart('/').TrimEnd('/');
        var prefix = trimmedPrefix.Length == 0 ? string.Empty : "/" + trimmedPrefix;
        var theme = options.Theme == BowireTheme.Dark ? "dark" : "light";
        var css = CssContent.Value;
        var js = JsContent.Value;

        var showInternal = options.ShowInternalServices ? "true" : "false";
        var lockServerUrl = options.LockServerUrl ? "true" : "false";
        var title = EscapeJs(options.Title);
        var desc = EscapeJs(options.Description);
        var serverUrl = options.ServerUrl is not null ? EscapeJs(options.ServerUrl) : "";

        // MapBasemap surfaces to the JS bundle as either a JSON string
        // ("\"satellite\"" / "\"osm\"" / a custom tile URL) or `null` when
        // the operator hasn't configured one — the widget's
        // bowireMapBasemapSpec() picks its built-in demotiles default
        // when the field is null/absent, so this is the right empty-state.
        var mapBasemap = string.IsNullOrEmpty(options.MapBasemap)
            ? "null"
            : "\"" + EscapeJs(options.MapBasemap) + "\"";

        // Build the serverUrls JSON array. Merge in the legacy single ServerUrl if
        // it isn't already in the list, so existing setups keep working unchanged.
        var allUrls = new List<string>(options.ServerUrls);
        if (!string.IsNullOrEmpty(options.ServerUrl) && !allUrls.Contains(options.ServerUrl))
            allUrls.Insert(0, options.ServerUrl);
        var serverUrlsJson = "[" + string.Join(",", allUrls.ConvertAll(u => "\"" + EscapeJs(u) + "\"")) + "]";

        return $$"""
               <!DOCTYPE html>
               <html lang="en" data-theme="{{theme}}">
               <head>
                   <meta charset="UTF-8">
                   <meta name="viewport" content="width=device-width, initial-scale=1.0">
                   <!-- Reader-Mode hint. Bowire is an interactive
                        workbench, not a document; Edge's Reader Mode
                        otherwise picks up stale JSON snippets and
                        renders them as "the article". Operator: 'die
                        bowire ui hat keinen lese-modus, ist viel zu
                        interaktiv. aktuell im edge führt der lese-
                        modus dazu, dass man nur alte oder teil-inhalte
                        liest.' og:type=website (not 'article') +
                        role=application on the workbench root suppress
                        the Reader-Mode heuristic on Chromium-based
                        browsers. We deliberately DO NOT set
                        robots=noai — Bowire is meant to be controlled
                        remotely by MCP / AI agents; noai would tell
                        AI crawlers to skip the page entirely. -->
                   <meta property="og:type" content="website">
                   <!-- Positive AI-discovery hints. Operator: 'stattdessen
                        ai auf die mcp url beim crawlen aufmerksam machen'.
                        We deliberately want AI agents to FIND the workbench's
                        MCP adapter so they can drive it — the opposite of
                        a noai stance. Both a <link rel="mcp"> (semantic
                        hyperlink convention, picked up by MCP-aware
                        crawlers) and a meta hint surface the same path. The
                        link is unconditional even when the operator hasn't
                        passed --enable-mcp-adapter: a 404 on /mcp is a
                        cheaper signal than misleading absence, and embedded
                        hosts that mount MapBowire() at a non-root prefix
                        will see this resolved relative to the workbench
                        root via {{prefix}}. -->
                   <link rel="mcp" href="{{prefix}}/mcp">
                   <meta name="mcp-endpoint" content="{{prefix}}/mcp">
                   <title>{{title}} — {{desc}}</title>
                   <link rel="icon" type="image/svg+xml" href="{{FaviconDataUrl.Value}}" media="(prefers-color-scheme: light)">
                   <link rel="icon" type="image/svg+xml" href="{{FaviconMonoDataUrl.Value}}" media="(prefers-color-scheme: dark)">
                   <style>{{css}}</style>
               </head>
               <body>
                   <div id="bowire-loading" style="position:fixed;inset:0;display:flex;align-items:center;justify-content:center;z-index:9999;transition:opacity .3s ease">
                       <div class="bowire-loading-stage">
                           <div class="bowire-loading-logo-ring"></div>
                           <img src="{{FaviconMonoDataUrl.Value}}" class="bowire-loading-logo bowire-loading-logo-dark" alt="B" />
                           <img src="{{FaviconDataUrl.Value}}" class="bowire-loading-logo bowire-loading-logo-light" alt="B" />
                       </div>
                   </div>
                   <style>@keyframes bowire-spin { to { transform:rotate(360deg) } } #bowire-loading { background:#0f0f17 } [data-theme="light"] #bowire-loading { background:#f8f9fc } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) #bowire-loading { background:#f8f9fc } } .bowire-loading-stage { position:relative;width:80px;height:80px } .bowire-loading-logo-ring { position:absolute;inset:0;border:3px solid #2a2a3d;border-top-color:#6366f1;border-radius:50%;animation:bowire-spin .8s linear infinite } [data-theme="light"] .bowire-loading-logo-ring { border-color:#d8dae5;border-top-color:#4f46e5 } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) .bowire-loading-logo-ring { border-color:#d8dae5;border-top-color:#4f46e5 } } .bowire-loading-logo { position:absolute;width:40px;height:40px;top:50%;left:50%;transform:translate(-50%,-50%) } .bowire-loading-logo-dark { display:block } .bowire-loading-logo-light { display:none } [data-theme="light"] .bowire-loading-logo-dark { display:none } [data-theme="light"] .bowire-loading-logo-light { display:block } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) .bowire-loading-logo-dark { display:none } html:not([data-theme="dark"]) .bowire-loading-logo-light { display:block } } #bowire-app { opacity:0;transition:opacity .2s ease } .bowire-app-ready { opacity:1!important }</style>
                   <div id="bowire-app" role="application" aria-label="Bowire workbench"></div>
                   <script>
                       window.__BOWIRE_CONFIG__ = {
                           title: "{{title}}",
                           description: "{{desc}}",
                           prefix: "{{prefix}}",
                           theme: "{{theme}}",
                           showInternalServices: {{showInternal}},
                           serverUrl: "{{serverUrl}}",
                           serverUrls: {{serverUrlsJson}},
                           lockServerUrl: {{lockServerUrl}},
                           embeddedMode: {{(options.Mode == BowireMode.Embedded ? "true" : "false")}},
                           autoCreateInitialWorkspace: {{(options.AutoCreateInitialWorkspace ? "true" : "false")}},
                           mapBasemap: {{mapBasemap}},
                           logoIcon: "{{FaviconDataUrl.Value}}",
                           logoIconMono: "{{FaviconMonoDataUrl.Value}}",
                           version: "{{AssemblyVersionString.Value}}",
                           kuestenlogikLockup: "{{KuestenlogikLockupDataUrl.Value}}",
                           kuestenlogikLockupMono: "{{KuestenlogikLockupMonoDataUrl.Value}}",
                           // #294 — rail + module catalogues. Each descriptor
                           // emitted by IBowireRailContribution / IBowireModuleContribution
                           // lands here as JSON; render-sidebar.js reads
                           // window.__BOWIRE_CONFIG__.rails to seed _railModes
                           // (replacing the old hardcoded array) and
                           // settings.js iterates the same list for the
                           // Rail-modes editor.
                           rails: {{_rails.ToJson()}},
                           modules: {{_modules.ToJson()}}
                       };
                   </script>
                   <script>{{js}}</script>
               </body>
               </html>
               """;
    }

    private static string ReadEmbeddedFile(string filename)
    {
        var assembly = typeof(BowireHtmlGenerator).Assembly;
        var resourceName = $"Kuestenlogik.Bowire.wwwroot.{filename}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Fallback: try reading from disk (development scenario)
            var dir = Path.GetDirectoryName(assembly.Location)!;
            var filePath = Path.Combine(dir, "wwwroot", filename);
            if (File.Exists(filePath))
                return File.ReadAllText(filePath);

            return $"/* {filename} not found */";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // #311 — per-package rail JS discovery. Each Rail.* (and
    // Security.*) NuGet ships its JS render code as an embedded
    // resource named `<asm>.wwwroot.js.<file>.js`. We scan every
    // loaded assembly matching that prefix, pull the resources,
    // and splice the concatenated payload into the placeholder
    // emitted by core's `_rail-fragments-marker.js`. Hosts that
    // don't reference a rail's package contribute zero bytes — the
    // bundle stays valid JS even with no rails at all.
    private const string RailFragmentMarkerBegin =
        "/*BOWIRE_RAIL_FRAGMENTS_BEGIN*/";
    private const string RailFragmentMarkerEnd =
        "/*BOWIRE_RAIL_FRAGMENTS_END*/";

    internal static string StitchRailFragments(string coreBundle)
    {
        var beginIdx = coreBundle.IndexOf(RailFragmentMarkerBegin, StringComparison.Ordinal);
        var endIdx = coreBundle.IndexOf(RailFragmentMarkerEnd, StringComparison.Ordinal);
        if (beginIdx < 0 || endIdx < 0 || endIdx < beginIdx)
        {
            // Marker missing (development bundle pre-#311 or someone
            // re-minified the bundle and stripped block comments) —
            // fall back to returning the core bundle as-is. The rail
            // surfaces just won't render in that case, which is
            // recoverable, whereas silently mangling the bundle would
            // not be.
            return coreBundle;
        }
        var insertAt = beginIdx + RailFragmentMarkerBegin.Length;
        var payload = CollectRailJsPayload();
        var sb = new System.Text.StringBuilder(coreBundle.Length + payload.Length + 2);
        sb.Append(coreBundle, 0, insertAt);
        sb.Append('\n');
        sb.Append(payload);
        sb.Append('\n');
        sb.Append(coreBundle, endIdx, coreBundle.Length - endIdx);
        return sb.ToString();
    }

    private static string CollectRailJsPayload()
    {
        var sb = new System.Text.StringBuilder();
        // Sort by assembly name so the splice is deterministic across
        // process restarts + machines. Within an assembly, sort by
        // resource name so a Rail.* that ships more than one JS file
        // also splices deterministically.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => IsRailLikeAssembly(a.GetName().Name))
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            string[] resources;
            try { resources = assembly.GetManifestResourceNames(); }
#pragma warning disable CA1031 // defensive — match BowireRailRegistry.Discover
            catch { continue; }
#pragma warning restore CA1031
            foreach (var name in resources.OrderBy(n => n, StringComparer.Ordinal))
            {
                // Convention: `<asm>.wwwroot.js.<file>.js`. We accept any
                // resource whose logical name contains `.wwwroot.js.`
                // and ends with `.js` — that lets a rail ship multiple
                // fragments (recordings + recordings-detail, &c.)
                // without each one needing a per-file registration.
                if (!name.Contains(".wwwroot.js.", StringComparison.Ordinal)) continue;
                if (!name.EndsWith(".js", StringComparison.Ordinal)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                sb.Append("\n/* --- ").Append(name).Append(" --- */\n");
                sb.Append(reader.ReadToEnd());
            }
        }
        return sb.ToString();
    }

    private static bool IsRailLikeAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Rail.* is the primary surface. Security.* uses the same
        // contract for the (future) JS slice that pairs with the
        // security rail descriptor. Map ships its own asset endpoint
        // and is intentionally excluded — its widget JS gets
        // dynamic-loaded by extensions.js, not stitched.
        return name.StartsWith("Kuestenlogik.Bowire.Rail.", StringComparison.Ordinal)
            || name.StartsWith("Kuestenlogik.Bowire.Security.", StringComparison.Ordinal);
    }

    private static string EscapeJs(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

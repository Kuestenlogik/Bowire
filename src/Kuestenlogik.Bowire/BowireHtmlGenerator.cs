// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Generates the Bowire HTML page with inlined CSS and JS.
/// Like Scalar, all assets are embedded — no static file serving needed.
/// </summary>
internal static class BowireHtmlGenerator
{
    private static readonly Lazy<string> CssContent = new(() => ReadEmbeddedFile("bowire.css"));
    private static readonly Lazy<string> JsContent = new(() => ReadEmbeddedFile("bowire.js"));

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
    private static readonly Lazy<string> FaviconBrowserTabDataUrl = new(() =>
    {
        var svg = ReadEmbeddedFile("favicon.svg");
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
    });

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
        var prefix = options.RoutePrefix.TrimStart('/').TrimEnd('/');
        var theme = options.Theme == BowireTheme.Dark ? "dark" : "light";
        var css = CssContent.Value;
        var js = JsContent.Value;

        var showInternal = options.ShowInternalServices ? "true" : "false";
        var lockServerUrl = options.LockServerUrl ? "true" : "false";
        var title = EscapeJs(options.Title);
        var desc = EscapeJs(options.Description);
        var serverUrl = options.ServerUrl is not null ? EscapeJs(options.ServerUrl) : "";

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
                   <title>{{title}} — {{desc}}</title>
                   <link rel="icon" type="image/svg+xml" href="{{FaviconBrowserTabDataUrl.Value}}">
                   <style>{{css}}</style>
               </head>
               <body>
                   <div id="bowire-loading" style="position:fixed;inset:0;display:flex;align-items:center;justify-content:center;z-index:9999;transition:opacity .3s ease">
                       <div style="display:flex;flex-direction:column;align-items:center">
                           <div class="bowire-loading-logo-ring">
                               <img src="{{FaviconMonoDataUrl.Value}}" class="bowire-loading-logo bowire-loading-logo-dark" alt="B" />
                               <img src="{{FaviconDataUrl.Value}}" class="bowire-loading-logo bowire-loading-logo-light" alt="B" />
                           </div>
                       </div>
                   </div>
                   <style>@keyframes bowire-spin { to { transform:rotate(360deg) } } #bowire-loading { background:#0f0f17 } [data-theme="light"] #bowire-loading { background:#f8f9fc } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) #bowire-loading { background:#f8f9fc } } .bowire-loading-logo-ring { width:80px;height:80px;border:3px solid #2a2a3d;border-top-color:#6366f1;border-radius:50%;animation:bowire-spin .8s linear infinite;display:flex;align-items:center;justify-content:center } [data-theme="light"] .bowire-loading-logo-ring { border-color:#d8dae5;border-top-color:#4f46e5 } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) .bowire-loading-logo-ring { border-color:#d8dae5;border-top-color:#4f46e5 } } .bowire-loading-logo { width:40px;height:40px;position:absolute } .bowire-loading-logo-dark { display:block } .bowire-loading-logo-light { display:none } [data-theme="light"] .bowire-loading-logo-dark { display:none } [data-theme="light"] .bowire-loading-logo-light { display:block } @media (prefers-color-scheme:light) { html:not([data-theme="dark"]) .bowire-loading-logo-dark { display:none } html:not([data-theme="dark"]) .bowire-loading-logo-light { display:block } } #bowire-app { opacity:0;transition:opacity .2s ease } .bowire-app-ready { opacity:1!important }</style>
                   <div id="bowire-app"></div>
                   <script>
                       window.__BOWIRE_CONFIG__ = {
                           title: "{{title}}",
                           description: "{{desc}}",
                           prefix: "/{{prefix}}",
                           theme: "{{theme}}",
                           showInternalServices: {{showInternal}},
                           serverUrl: "{{serverUrl}}",
                           serverUrls: {{serverUrlsJson}},
                           lockServerUrl: {{lockServerUrl}},
                           embeddedMode: {{(options.Mode == BowireMode.Embedded ? "true" : "false")}},
                           logoIcon: "{{FaviconDataUrl.Value}}",
                           logoIconMono: "{{FaviconMonoDataUrl.Value}}",
                           version: "{{AssemblyVersionString.Value}}",
                           kuestenlogikLockup: "{{KuestenlogikLockupDataUrl.Value}}",
                           kuestenlogikLockupMono: "{{KuestenlogikLockupMonoDataUrl.Value}}"
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

    private static string EscapeJs(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp.Apps;

/// <summary>
/// #811 — one MCP App Bowire's own server ships: a tool's answer drawn as UI
/// by a host that declared the <c>io.modelcontextprotocol/ui</c> extension.
/// The app is one HTML file with the view side of the ext-apps bridge inlined,
/// so it loads nothing from anywhere and runs under a policy that grants no
/// origin and no permission.
/// </summary>
/// <param name="ResourceUri">The <c>ui://</c> resource the tool names.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Description">What the app shows.</param>
/// <param name="Template">The embedded HTML, under <c>Apps/</c>.</param>
public sealed record BowireMcpApp(string ResourceUri, string Name, string Description, string Template)
{
    /// <summary>The MIME type of an MCP App's document.</summary>
    public const string MimeType = "text/html;profile=mcp-app";

    /// <summary>Where a template takes the view library.</summary>
    internal const string ViewPlaceholder = "<!-- bowire-app-view -->";

    private static readonly Regex s_scriptEnd = new("</script", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Lazy<string> s_view = new(() => Read("Kuestenlogik.Bowire.Mcp.Apps.mcp-app-view.js"));

    /// <summary>
    /// The app's document: the template with the view library inlined in a
    /// script element (any <c>&lt;/script</c> inside it escaped, so it cannot
    /// end the element early).
    /// </summary>
    public string Html()
    {
        var view = s_scriptEnd.Replace(s_view.Value, "<\\/script");
        var html = Read("Kuestenlogik.Bowire.Mcp.Apps." + Template);
        if (!html.Contains(ViewPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Template} has no {ViewPlaceholder} to take the view library.");
        return html.Replace(ViewPlaceholder, "<script>\n" + view + "\n</script>", StringComparison.Ordinal);
    }

    /// <summary>
    /// The resource the host reads: the document, with <c>_meta.ui</c>
    /// declaring no origin and no permission.
    /// </summary>
    public McpServerResource Resource()
    {
        var resource = McpServerResource.Create(
            () => new TextResourceContents { Uri = ResourceUri, MimeType = MimeType, Text = Html(), Meta = UiMeta() },
            new McpServerResourceCreateOptions { UriTemplate = ResourceUri, Name = Name, Description = Description, MimeType = MimeType });
        return McpApps.SetResourceUi(resource, new McpUiResourceMeta { Csp = new McpUiResourceCsp(), PrefersBorder = true });
    }

    // The same declaration on the read contents, where a host looks for it
    // when it renders: nothing to connect to, load from, frame or base on.
    private static JsonObject UiMeta() => new()
    {
        ["ui"] = new JsonObject
        {
            ["csp"] = new JsonObject
            {
                ["connectDomains"] = new JsonArray(),
                ["resourceDomains"] = new JsonArray(),
                ["frameDomains"] = new JsonArray(),
                ["baseUriDomains"] = new JsonArray(),
            },
            ["prefersBorder"] = true,
        },
    };

    private static string Read(string logicalName)
    {
        using var stream = typeof(BowireMcpApp).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"The embedded resource {logicalName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

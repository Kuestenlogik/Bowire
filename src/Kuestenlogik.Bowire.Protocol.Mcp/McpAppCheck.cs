// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// #812 — the <c>Apps</c> service: one method per tool that names an MCP App.
/// Invoking it checks the app's declaration the way a host would load it: the
/// tool still names a <c>ui://</c> resource, the resource resolves, and it is
/// an HTML document of the MCP App type. The answer is a plain object, so a
/// <c>bowire test</c> assertion or a flow expectation reads it like any
/// response; a broken app fails the call with a message that says which part.
/// </summary>
/// <remarks>
/// It lists the tools again rather than trusting discovery, so a run tests
/// the server as it is now. The listing declares the Apps extension, since a
/// server may show <c>_meta.ui</c> only to a client that hosts apps.
/// </remarks>
internal static class McpAppCheck
{
    /// <summary>The service's name in the tree and on the wire.</summary>
    public const string ServiceName = "Apps";

    /// <summary>The service discovery lists, or null when no tool names an app.</summary>
    public static BowireServiceInfo? Describe(IEnumerable<BowireMethodInfo> tools, string serverUrl)
    {
        var methods = tools.Where(t => !string.IsNullOrWhiteSpace(t.UiResourceUri)).Select(t => new BowireMethodInfo(
            t.Name, $"{ServiceName}/{t.Name}", ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("AppCheckRequest", "mcp.AppCheckRequest", []),
            OutputType: new BowireMessageInfo("AppCheck", "mcp.AppCheck", []),
            MethodType: "Unary")
        {
            Summary = $"Check the MCP App of {t.Name} ({t.UiResourceUri})",
            // No UiResourceUri here: the check answers about the app, it does not show it.
            Description = $"Lists the tools again, reads {t.UiResourceUri} and answers with resourceUri, mimeType, isMcpApp, isHtml, bytes, csp, permissions and visibility. Fails when the tool names no app or its resource does not load.",
        }).ToList();

        return methods.Count == 0 ? null : new BowireServiceInfo(ServiceName, "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP Apps — one check per tool with an app: does the tool still name it, does it load, is it an MCP App document. Assert on the answer in a test or a flow.",
        };
    }

    /// <summary>Check the app of <paramref name="toolName"/>.</summary>
    public static async Task<JsonElement> RunAsync(McpClient client, string toolName, CancellationToken ct)
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The server lists no tool named '{toolName}'.");

        var uri = BowireMcpProtocol.UiResourceUriOf(tool.ProtocolTool.Meta)
            ?? throw new InvalidOperationException($"The tool '{toolName}' names no MCP App: it has no _meta.ui.resourceUri.");

        ReadResourceResult read;
        try
        {
            read = await client.ReadResourceAsync(new Uri(uri, UriKind.RelativeOrAbsolute), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            throw new InvalidOperationException($"The tool '{toolName}' names the app {uri}, which the server does not serve: {ex.Message}", ex);
        }

        var content = read.Contents.FirstOrDefault()
            ?? throw new InvalidOperationException($"The app {uri} of '{toolName}' resolved to no content.");
        var text = content switch
        {
            TextResourceContents t => t.Text,
            BlobResourceContents b => DecodeBlob(b),
            _ => "",
        };
        var mimeType = content.MimeType ?? "";
        var ui = content.Meta?["ui"] as JsonObject;

        var answer = new JsonObject
        {
            ["tool"] = toolName,
            ["resourceUri"] = uri,
            ["mimeType"] = mimeType,
            ["isMcpApp"] = string.Equals(mimeType.Replace(" ", "", StringComparison.Ordinal), BowireMcpProtocol.AppsMimeType, StringComparison.OrdinalIgnoreCase),
            ["isHtml"] = mimeType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && text.Contains('<', StringComparison.Ordinal),
            ["bytes"] = Encoding.UTF8.GetByteCount(text),
            ["csp"] = ui?["csp"]?.DeepClone(),
            ["permissions"] = ui?["permissions"]?.DeepClone(),
            ["visibility"] = (tool.ProtocolTool.Meta?["ui"] as JsonObject)?["visibility"]?.DeepClone(),
        };
        return JsonSerializer.SerializeToElement(answer);
    }

    private static string DecodeBlob(BlobResourceContents blob)
    {
        try { return Encoding.UTF8.GetString(blob.DecodedData.Span); }
        catch (FormatException) { return ""; }
    }
}

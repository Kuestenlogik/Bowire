// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// #812 — the MCP Apps a server ships, checked as what they are: third-party
/// HTML that a host runs next to a model with tool access. Rolls up to
/// <c>API8:2023 — Security Misconfiguration</c>, because every finding is a
/// declaration the server could have made narrower.
///
/// <para>For each tool with a <c>_meta.ui.resourceUri</c> the probe reads the
/// <c>ui://</c> resource (a read, never a tool call) and reports:</para>
/// <list type="bullet">
/// <item>a <c>_meta.ui.csp</c> list that allows any origin (<c>*</c>, a bare
/// scheme like <c>https:</c>, or <c>https://*</c>), naming the tool and the
/// CSP directive it lands in;</item>
/// <item><c>'unsafe-eval'</c> (or <c>'unsafe-inline'</c>) smuggled into a
/// domain list;</item>
/// <item><c>permissions</c> for camera, microphone or geolocation;</item>
/// <item>a <c>&lt;script src&gt;</c> from an origin the app's
/// <c>resourceDomains</c> does not list;</item>
/// <item>destructive tools the app may call itself (visibility includes
/// <c>app</c>): the host forwards such a call without the model, and often
/// without the user, in between.</item>
/// </list>
/// <para>No app on the server skips; apps with none of the above are Safe.</para>
/// </summary>
internal sealed class McpAppsProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API8:2023");

    public string ProtocolId => "mcp";

    // The CSP lists of _meta.ui.csp and the directives a host builds from them.
    private static readonly (string Key, string Directive)[] s_lists =
    [
        ("connectDomains", "connect-src"),
        ("resourceDomains", "script-src / style-src / img-src / font-src / media-src"),
        ("frameDomains", "frame-src"),
        ("baseUriDomains", "base-uri"),
        ("baseUris", "base-uri"),
    ];

    private static readonly string[] s_sensitivePermissions = ["camera", "microphone", "geolocation"];

    private static readonly Regex s_scriptSrc = new(@"<script\b[^>]*\bsrc\s*=\s*[""']?(?<url>[^""'\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        List<BowireServiceInfo> surfaces;
        try
        {
            surfaces = await protocol.DiscoverAsync(target, showInternalServices: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API8-MCP-APPS-UNREACHABLE", "MCP Apps probe skipped",
                $"MCP discovery could not be attempted ({ex.GetType().Name}).")];
        }

        var tools = surfaces.Where(s => s.Name == "Tools").SelectMany(s => s.Methods).ToList();
        var apps = tools.Where(m => !string.IsNullOrWhiteSpace(m.UiResourceUri)).ToList();
        if (apps.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API8-MCP-APPS-NONE", "No MCP Apps",
                "No tool on this server names a ui:// resource (_meta.ui.resourceUri), so there is no MCP App to check.")];
        }

        var metadata = ToMetadata(authHeaders);
        var findings = new List<ScanFinding>();
        foreach (var uri in apps.Select(a => a.UiResourceUri!).Distinct(StringComparer.Ordinal))
        {
            var toolNames = string.Join(", ", apps.Where(a => a.UiResourceUri == uri).Select(a => a.Name));
            JsonElement? content;
            try
            {
                var read = await protocol.InvokeAsync(target, "Resources", uri, [], showInternalServices: false, metadata, ct).ConfigureAwait(false);
                content = FirstContent(read.Response);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                content = null;
                findings.Add(Marker(ScanFindingStatus.Skipped, "API8-MCP-APP-UNREADABLE", "MCP App resource unreadable",
                    $"The app {uri} of {toolNames} could not be read ({ex.GetType().Name}); its declaration was not checked."));
                continue;
            }
            if (content is null)
            {
                findings.Add(Marker(ScanFindingStatus.Skipped, "API8-MCP-APP-UNREADABLE", "MCP App resource unreadable",
                    $"The app {uri} of {toolNames} returned no content; its declaration was not checked."));
                continue;
            }
            findings.AddRange(CheckApp(uri, toolNames, content.Value));
        }

        var destructive = tools.Where(m => m.Destructive && (m.UiVisibility is null || m.UiVisibility.Contains("app", StringComparer.Ordinal))).ToList();
        if (destructive.Count > 0)
        {
            findings.Add(Finding("BWR-OWASP-API8-MCP-APP-DESTRUCTIVE-TOOL", "MCP App can call destructive tools",
                $"The server marks {string.Join(", ", destructive.Select(t => t.Name))} as destructive (annotations.destructiveHint) and lets apps call {(destructive.Count == 1 ? "it" : "them")} ({(destructive.All(t => t.UiVisibility is null) ? "no _meta.ui.visibility, which means model and app" : "_meta.ui.visibility includes app")}). An app's tools/call goes from the HTML to the host to the server without the model deciding, so a button in the app, or script injected into it, can trigger the change.",
                "Set _meta.ui.visibility to [\"model\"] on destructive tools, so only the model (and the host's approval) can call them; give the app read-only tools, or a tool that only prepares a change the model then confirms.",
                "medium", 5.4, "CWE-862"));
        }

        // Safe only when every app was read and checked: an unread app is not a pass.
        if (findings.All(f => f.Status != ScanFindingStatus.Vulnerable && f.Status != ScanFindingStatus.Skipped))
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API8-MCP-APPS-OK", "MCP Apps declared narrowly",
                $"{apps.Count} tool(s) with an MCP App; no app allows any origin or eval, asks for camera, microphone or geolocation, or loads a script it did not declare, and no destructive tool is open to apps."));
        }
        return findings;
    }

    internal IEnumerable<ScanFinding> CheckApp(string uri, string toolNames, JsonElement content)
    {
        var ui = Get(Get(content, "_meta"), "ui");
        var csp = Get(ui, "csp");
        var resourceDomains = new List<string>();

        foreach (var (key, directive) in s_lists)
        {
            if (Get(csp, key) is not { ValueKind: JsonValueKind.Array } list) continue;
            var values = list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.Trim()).ToList();
            if (key == "resourceDomains") resourceDomains.AddRange(values);

            var any = values.Where(IsAnyOrigin).ToList();
            if (any.Count > 0)
            {
                yield return Finding("BWR-OWASP-API8-MCP-APP-CSP-ANY-ORIGIN", "MCP App allows any origin",
                    $"The app {uri} of {toolNames} lists {string.Join(", ", any.Select(v => "`" + v + "`"))} in _meta.ui.csp.{key}, which a host turns into {directive}. The app may then {(key == "connectDomains" ? "send what it sees, tool results included, to any server" : key == "resourceDomains" ? "load script from any server" : key == "frameDomains" ? "frame any page" : "rebase its URLs on any origin")}.",
                    $"List the exact origins the app needs in _meta.ui.csp.{key} (scheme, host, port), or none.",
                    key is "connectDomains" or "resourceDomains" ? "medium" : "low", key is "connectDomains" or "resourceDomains" ? 5.3 : 3.7, "CWE-16");
            }
            var unsafeKeywords = values.Where(v => v.Contains("unsafe-eval", StringComparison.OrdinalIgnoreCase) || v.Contains("unsafe-inline", StringComparison.OrdinalIgnoreCase)).ToList();
            if (unsafeKeywords.Count > 0)
            {
                yield return Finding("BWR-OWASP-API8-MCP-APP-CSP-UNSAFE", "MCP App asks for unsafe script evaluation",
                    $"The app {uri} of {toolNames} puts {string.Join(", ", unsafeKeywords.Select(v => "`" + v + "`"))} into _meta.ui.csp.{key} ({directive}). A host that copies the list into its policy lets the app evaluate strings as code.",
                    "Remove CSP keywords from the domain lists; they take origins only. Build the app without eval (for example zod in jitless mode).",
                    "medium", 5.3, "CWE-95");
            }
        }

        if (Get(ui, "permissions") is { ValueKind: JsonValueKind.Object } permissions)
        {
            var asked = s_sensitivePermissions.Where(p => permissions.TryGetProperty(p, out _)).ToList();
            if (asked.Count > 0)
            {
                yield return Finding("BWR-OWASP-API8-MCP-APP-PERMISSIONS", "MCP App asks for sensitive device access",
                    $"The app {uri} of {toolNames} asks for {string.Join(", ", asked)} in _meta.ui.permissions. Granted, it can record or locate the user while a model with tool access runs beside it.",
                    "Ask only for the permissions the app's purpose needs, and say in the tool's description why.",
                    "low", 3.5, "CWE-250");
            }
        }

        var html = Get(content, "text") is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
        var undeclared = s_scriptSrc.Matches(html).Select(m => m.Groups["url"].Value)
            .Select(OriginOf).Where(o => o is not null && !resourceDomains.Any(d => Covers(d, o!))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (undeclared.Count > 0)
        {
            yield return Finding("BWR-OWASP-API8-MCP-APP-UNDECLARED-SCRIPT", "MCP App loads script it did not declare",
                $"The app {uri} of {toolNames} loads script from {string.Join(", ", undeclared)}, which its _meta.ui.csp.resourceDomains does not list. A strict host blocks it and the app breaks; a lax host runs code the declaration does not mention.",
                "Add the script's origin to _meta.ui.csp.resourceDomains, or better, bundle the script into the app.",
                "low", 3.1, "CWE-829");
        }
    }

    internal static bool IsAnyOrigin(string value)
    {
        var v = value.Trim().TrimEnd('/');
        return v == "*" || Regex.IsMatch(v, @"^[a-z][a-z0-9+.-]*:(//\*)?$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? $"{u.Scheme}://{u.Authority}" : null;

    private static bool Covers(string declared, string origin)
    {
        var d = declared.Trim().TrimEnd('/');
        if (IsAnyOrigin(d) || string.Equals(d, origin, StringComparison.OrdinalIgnoreCase)) return true;
        // https://*.example.com covers https://cdn.example.com
        var star = d.IndexOf("://*.", StringComparison.Ordinal);
        return star > 0 && origin.StartsWith(d[..(star + 3)], StringComparison.OrdinalIgnoreCase)
            && origin.EndsWith(d[(star + 4)..], StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement? FirstContent(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (Get(doc.RootElement, "contents") is not { ValueKind: JsonValueKind.Array } contents || contents.GetArrayLength() == 0) return null;
            return contents[0].Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Get(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    private static Dictionary<string, string>? ToMetadata(IList<string> authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            if (name.Length > 0) md[name] = raw[(colon + 1)..].TrimStart();
        }
        return md.Count > 0 ? md : null;
    }

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss, string cwe) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: cwe, owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the MCP Apps probe."),
        Status = status,
        Detail = detail,
    };
}

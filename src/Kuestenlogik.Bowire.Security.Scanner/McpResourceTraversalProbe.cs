// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for MCP (Model Context Protocol), rolling up to
/// <c>API1:2023 — Broken Object Level Authorization</c> (CWE-22, Path Traversal).
/// An MCP server's resource seam serves file contents by URI: the client asks
/// for a resource and the server reads it back. A server that resolves a
/// <c>file://</c> or <c>..</c>-laden URI without validating it against an
/// allow-list of roots lets a client read arbitrary host files — the resource
/// analog of directory traversal, and object-level authorization broken at the
/// resource boundary (any client reads any file the server process can).
///
/// <para>Black-box and non-destructive. The probe first confirms the endpoint
/// actually speaks MCP (<see cref="IBowireProtocol.DiscoverAsync"/> completes
/// the initialize handshake and lists a surface), then asks the resource seam
/// to read a small set of traversal / out-of-scope URIs. A read that comes back
/// with resource content means the server escaped its intended scope
/// (Vulnerable); a server that rejects every traversal URI enforces the boundary
/// (Safe). A non-MCP or unreachable endpoint skips rather than reporting a false
/// pass.</para>
/// </summary>
internal sealed class McpResourceTraversalProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API1:2023");

    public string ProtocolId => "mcp";

    /// <summary>
    /// Traversal / out-of-scope resource URIs. Deliberately targets stock,
    /// harmless-to-read files (<c>/etc/passwd</c>, <c>win.ini</c>) so a
    /// successful read proves scope escape without exposing anything sensitive
    /// the probe itself cares about — it detects the missing boundary, it does
    /// not exfiltrate. Both absolute <c>file://</c> paths and relative
    /// <c>..</c>-climbing forms are tried, since servers differ in which they
    /// normalise.
    /// </summary>
    private static readonly string[] s_traversalUris =
    [
        "file:///etc/passwd",
        "file:///c:/windows/win.ini",
        "file://../../../../etc/passwd",
        "../../../../../../etc/passwd",
    ];

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // 1. Preflight: confirm this is really an MCP endpoint. DiscoverAsync
        //    runs the initialize handshake and lists the server's surface; an
        //    empty list means the endpoint never completed an MCP handshake, so
        //    the resource seam can't be confirmed — skip rather than emit a
        //    false "traversal blocked" pass against a non-MCP target.
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
            // DiscoverAsync normally swallows its own failures and returns [],
            // but guard the seam anyway.
            return [Marker(ScanFindingStatus.Skipped, "API1-MCP-UNREACHABLE", "MCP resource-traversal probe skipped",
                $"MCP discovery could not be attempted ({ex.GetType().Name}) — the resource-traversal seam can't be confirmed.")];
        }

        if (surfaces.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-MCP-NOT-MCP", "MCP resource-traversal probe skipped",
                "An anonymous MCP handshake listed nothing — the target did not complete an MCP handshake, so the resource-read seam can't be confirmed (avoiding a false pass against a non-MCP endpoint).")];
        }

        // 2. Traversal attempts: ask the resource seam to read each out-of-scope
        //    URI. The first read that returns resource content is a hit — stop
        //    there. A null Response means the server rejected the read; move on.
        var metadata = ToMetadata(authHeaders);
        foreach (var uri in s_traversalUris)
        {
            InvokeResult r;
            try
            {
                r = await protocol.InvokeAsync(target, "Resources", uri, new List<string>(),
                    showInternalServices: false, metadata, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A thrown read (transport wobble) is not evidence the boundary
                // held; skip the whole probe rather than mis-report Safe.
                return [Marker(ScanFindingStatus.Skipped, "API1-MCP-UNREACHABLE", "MCP resource-traversal probe skipped",
                    $"An MCP resource read failed to complete ({ex.GetType().Name}) — the resource-traversal seam can't be confirmed.")];
            }

            if (r.Response is not null && HasResourceContent(r.Response))
            {
                return [Finding("BWR-OWASP-API1-MCP-TRAVERSAL", "MCP server serves files via path traversal",
                    $"An MCP resource read for `{uri}` returned file content — the read escaped its intended scope to disclose an arbitrary host file. The resource seam applies no allow-list / traversal validation, so any client can read any file the server process can reach (arbitrary file disclosure through the MCP resource boundary).",
                    "Validate and normalise resource URIs against an allow-list of roots: reject `..` path segments and absolute `file://` paths that resolve outside the configured resource directory, and enforce per-client authorization on every resource read. Never serve resource content straight from a client-supplied path.",
                    "high", 7.5)];
            }
        }

        // 3. Every traversal URI was rejected (all Response null) — the server
        //    refused to read out-of-scope resources.
        return [Marker(ScanFindingStatus.Safe, "API1-MCP-TRAVERSAL-BLOCKED", "MCP resource traversal blocked",
            $"The MCP server rejected all {s_traversalUris.Length} traversal / out-of-scope resource URIs — the resource seam enforces its boundary (the desired production state).")];
    }

    /// <summary>
    /// Decide whether an MCP resource-read response actually carries file
    /// content. A successful <c>ReadResourceAsync</c> serialises a
    /// <c>ReadResourceResult</c> whose <c>contents</c> (case-insensitive) array
    /// holds the resource-content items (text or blob). We treat a hit as a root
    /// object with a non-empty <c>contents</c> / <c>Contents</c> array — a clear,
    /// unambiguous signal that the read returned data. When that array is absent
    /// or empty (e.g. an error envelope, or <c>{"contents":[]}</c>) we prefer NOT
    /// to flag, so an inconclusive shape can never produce a false Vulnerable.
    /// </summary>
    private static bool HasResourceContent(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in root.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "contents", StringComparison.OrdinalIgnoreCase))
                    continue;
                return prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0;
            }

            return false;
        }
        catch (JsonException)
        {
            // Unparseable response — don't risk a false positive.
            return false;
        }
    }

    /// <summary>
    /// Fold the scan's <c>--auth-header</c> values (<c>Name: Value</c> strings)
    /// into a metadata dictionary the plugin forwards as request headers, so an
    /// authenticated MCP server answers the resource read instead of bouncing it.
    /// </summary>
    private static Dictionary<string, string>? ToMetadata(IList<string> authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length > 0) md[name] = value;
        }
        return md.Count > 0 ? md : null;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-22", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the MCP resource-traversal probe."),
        Status = status,
        Detail = detail,
    };
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for MCP (#400), rolling up to <c>API5:2023 — Broken Function
/// Level Authorization</c>. The deterministic, static-inventory slice of the
/// tool-call-injection concern: rather than sending an adversarial prompt (an
/// AI-semantics problem that belongs with the AI security-scan orchestration,
/// #104/#106), it lists the server's tools <em>anonymously</em> and flags
/// destructive / mutating tools that are reachable with no function-level
/// authorization gate. Those are exactly the tools a prompt-injection attack
/// coerces an agent into calling — exposing them unauthenticated, with no
/// machine-readable safety hint, is the pre-condition for tool-call abuse.
///
/// <para>Discovery-only (lists, never invokes — no side effect), so it stays in
/// the default passive scan. Classification is a name heuristic: honest about
/// being a heuristic, it names the tools it flags so an operator can confirm
/// they're gated. A non-MCP endpoint or one that gates discovery returns
/// nothing and the probe skips.</para>
/// </summary>
internal sealed class McpToolInjectionProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API5:2023");

    public string ProtocolId => "mcp";

    // Verb tokens that mark a tool as destructive / state-mutating. Matched
    // against the tool name split into tokens, so "delete_file" / "runShell"
    // hit but "search" / "get_user" / "list_files" don't.
    private static readonly HashSet<string> s_destructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "remove", "drop", "destroy", "purge", "wipe", "truncate", "clear",
        "write", "update", "modify", "edit", "patch", "set", "create", "insert", "add", "append",
        "exec", "execute", "run", "shell", "spawn", "eval", "command", "cmd", "sql", "script",
        "kill", "terminate", "stop", "restart", "reboot", "shutdown",
        "send", "email", "transfer", "pay", "deploy", "publish", "push",
        "revoke", "grant", "chmod", "chown", "move", "rename", "reset", "format", "install", "uninstall", "upload",
    };

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        List<BowireServiceInfo> surfaces;
        try
        {
            // Anonymous listing — the plugin sends no credentials on discovery.
            surfaces = await protocol.DiscoverAsync(target, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-MCP-UNREACHABLE", "MCP tool-injection probe skipped",
                $"Anonymous MCP discovery could not be attempted ({ex.GetType().Name}) — target may not be an MCP server or gates the handshake.")];
        }

        var tools = surfaces.SelectMany(s => s.Methods.Select(m => m.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tools.Length == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-MCP-NO-TOOLS", "MCP tool-injection probe skipped — no tools",
                "An anonymous MCP handshake returned no tools — the target is not an MCP server, or it gates discovery behind authentication.")];
        }

        var destructive = tools.Where(IsDestructive).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (destructive.Length == 0)
        {
            return [Marker(ScanFindingStatus.Safe, "API5-MCP-NO-DESTRUCTIVE-TOOLS",
                "No destructive MCP tools exposed anonymously",
                $"{tools.Length} tool(s) were enumerable anonymously, but none look destructive / state-mutating by name — no obvious tool-call-injection lever.")];
        }

        var sample = string.Join(", ", destructive.Take(10));
        return [Finding("BWR-OWASP-API5-MCP-TOOL-INJECTION", "Destructive MCP tools exposed without function-level authorization",
            $"{destructive.Length} of {tools.Length} anonymously-listed MCP tool(s) look destructive / state-mutating — e.g. {sample}. Exposed with no per-tool authorization gate, these are the levers a prompt-injection attack coerces an agent into invoking (tool-call abuse / privilege escalation beyond the requested scope).",
            "Gate destructive tools behind authentication + an explicit confirmation step, apply least privilege (expose only the tools a given client needs), and annotate mutating tools with the MCP `destructiveHint` so hosts can require human approval. The deterministic, adversarial-payload variant of this check belongs with the AI security-scan orchestration (#104 / #106).",
            "high", 7.1)];
    }

    // A tool is destructive if any of its name tokens is a destructive verb.
    private static bool IsDestructive(string toolName)
    {
        foreach (var token in Tokenize(toolName))
            if (s_destructiveVerbs.Contains(token)) return true;
        return false;
    }

    // Split on non-alphanumeric boundaries AND camelCase humps: "runShell",
    // "delete_file", "Files.Delete" → individual lowercase-comparable tokens.
    private static IEnumerable<string> Tokenize(string name)
    {
        var current = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else
            {
                // camelCase / PascalCase boundary: a new upper after a lower.
                if (char.IsUpper(c) && current.Length > 0 && char.IsLower(prev))
                {
                    yield return current.ToString();
                    current.Clear();
                }
                current.Append(c);
            }
            prev = c;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-862", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the MCP tool-injection inventory probe."),
        Status = status,
        Detail = detail,
    };
}

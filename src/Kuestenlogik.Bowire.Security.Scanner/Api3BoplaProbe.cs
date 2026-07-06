// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API3:2023 — Broken Object Property Level Authorization</c>
/// (mass assignment). Rather than attempting real privilege escalation
/// (<c>isAdmin:true</c>, <c>role:admin</c>) — which would actually mutate the
/// target in a dangerous way — it PATCHes the object with a single harmless,
/// unknown <b>canary</b> property and checks whether the server accepts and
/// <b>persists</b> it. A server that stores an arbitrary client-supplied
/// property is vulnerable to the whole mass-assignment class (an attacker
/// would send <c>isAdmin</c> instead of the canary). Best-effort cleanup
/// nulls the canary afterwards.
/// </summary>
/// <remarks>
/// Active + mutating by nature: it writes to the target. Gated on the target
/// being a readable JSON object (GET → 2xx object body) so it only runs where
/// there's an object to mass-assign against; write denials (401/403) and
/// PATCH-unsupported (405) are reported as Skipped, not clean.
/// </remarks>
internal sealed class Api3BoplaProbe : IOwaspApiProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API3:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        // Baseline: the target must be a readable JSON object to mass-assign against.
        var (statusG, bodyG) = await SendAsync(http, HttpMethod.Get, target, null, authHeaders, ct).ConfigureAwait(false);
        if (statusG is < 200 or >= 300 || !LooksLikeJsonObject(bodyG))
        {
            return [Marker(ScanFindingStatus.Skipped, "API3-NO-OBJECT",
                "API3 needs a readable JSON object target",
                $"GET {target} did not return a JSON object (HTTP {statusG}). Point --target at an object resource you can read + PATCH so mass assignment can be tested.")];
        }

        var canaryKey = "bowireProbe" + Guid.NewGuid().ToString("N")[..8];
        var canaryVal = "bwr-" + Guid.NewGuid().ToString("N")[..12];
        var patchBody = $"{{\"{canaryKey}\":\"{canaryVal}\"}}";

        var (statusP, bodyP) = await SendAsync(http, HttpMethod.Patch, target, patchBody, authHeaders, ct).ConfigureAwait(false);

        // Interpret the write.
        if (statusP is 401 or 403)
        {
            return [Marker(ScanFindingStatus.Skipped, "API3-WRITE-DENIED", "Write denied — can't test mass assignment",
                $"PATCH was denied (HTTP {statusP}). Supply --auth-header for an identity that can write to the object so the property-level check can run.")];
        }
        if (statusP is 405 or 501 or 404)
        {
            return [Marker(ScanFindingStatus.Skipped, "API3-NO-PATCH", "Target doesn't accept PATCH",
                $"PATCH returned HTTP {statusP}. Point --target at an endpoint that accepts a partial-update PATCH so mass assignment can be tested.")];
        }
        if (statusP is 400 or 422)
        {
            return [Marker(ScanFindingStatus.Safe, "API3-CLEAN-REJECTED", "Unknown property rejected",
                $"The server rejected a request with an unknown property (HTTP {statusP}) — it validates object properties, so arbitrary client-set fields don't stick.")];
        }
        if (statusP is < 200 or >= 300)
        {
            return [Marker(ScanFindingStatus.Skipped, "API3-INCONCLUSIVE", "Mass-assignment test inconclusive",
                $"PATCH returned HTTP {statusP} — neither a clear accept nor a validation rejection. Can't conclude on mass assignment.")];
        }

        // 2xx — did the canary persist? A follow-up GET is the strong signal
        // (reflection in the PATCH response alone could be a mere echo).
        var (_, bodyG2) = await SendAsync(http, HttpMethod.Get, target, null, authHeaders, ct).ConfigureAwait(false);
        var persisted = bodyG2.Contains(canaryVal, StringComparison.Ordinal);
        var reflected = bodyP.Contains(canaryVal, StringComparison.Ordinal);

        // Best-effort cleanup: null the canary back out.
        _ = await SendAsync(http, HttpMethod.Patch, target, $"{{\"{canaryKey}\":null}}", authHeaders, ct).ConfigureAwait(false);

        if (persisted)
        {
            return [Finding("BWR-OWASP-API3-MASS-ASSIGN", "Mass assignment — arbitrary client property persisted",
                $"A PATCH with an unknown property '{canaryKey}' was stored on the object (confirmed by a follow-up GET). The server binds client input straight onto the model, so an attacker could set privileged fields (isAdmin, role, balance, ownerId) the same way.",
                "Bind requests to an explicit allow-list DTO; never bind the raw request onto the persistence model. Reject or ignore unknown properties, and mark sensitive fields read-only / server-controlled.",
                "high", 7.6)];
        }
        if (reflected)
        {
            return [Finding("BWR-OWASP-API3-REFLECTED", "Unknown property echoed on write (possible mass assignment)",
                $"A PATCH with an unknown property '{canaryKey}' was echoed in the response but not confirmed persisted. The endpoint may bind unknown client properties — verify sensitive fields aren't writable this way.",
                "Bind to an explicit allow-list DTO and reject unknown properties; confirm sensitive fields are server-controlled.",
                "medium", 5.3)];
        }
        return [Marker(ScanFindingStatus.Safe, "API3-CLEAN-IGNORED", "Unknown property not persisted",
            "The server accepted the PATCH but did not persist the unknown canary property — arbitrary client-set fields don't stick.")];
    }

    // ---- helpers ----

    private static bool LooksLikeJsonObject(string body)
        => !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{');

    private static async Task<(int Status, string Body)> SendAsync(HttpClient http, HttpMethod method, string url, string? jsonBody, IList<string> authHeaders, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return (-1, "");
        }
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API3 suite row."),
        Status = status,
        Detail = detail,
    };
}

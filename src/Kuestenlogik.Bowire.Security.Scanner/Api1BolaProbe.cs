// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API1:2023 — Broken Object Level Authorization</c>. A
/// real BOLA test needs two identities, so this probe runs only when both
/// <c>--auth-header</c> (identity A) and <c>--auth-header-b</c> (identity B)
/// are supplied and <c>--target</c> points at an object-scoped URL (a path
/// ending in a numeric or UUID id). It reads the object as A, confirms
/// anonymous access is blocked, then reads the same object as B: if B — a
/// different authenticated identity — can read A's object while anonymous is
/// denied, the endpoint is missing an object-level authorization check.
/// </summary>
internal sealed partial class Api1BolaProbe : IOwaspApiProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API1:2023");

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        if (authHeaders is null || authHeaders.Count == 0 || authHeadersB is null || authHeadersB.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-NEEDS-TWO-IDENTITIES", "API1 BOLA needs two identities",
                "Supply both --auth-header (identity A) and --auth-header-b (identity B). BOLA is a cross-identity check: it verifies identity B cannot read identity A's object.")];
        }
        if (SameIdentity(authHeaders, authHeadersB))
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-SAME-IDENTITY", "API1 BOLA needs two DIFFERENT identities",
                "--auth-header and --auth-header-b are identical — supply two different identities so the cross-identity check is meaningful.")];
        }

        Uri uri;
        try { uri = new Uri(target); }
        catch (UriFormatException)
        {
            return [Marker(ScanFindingStatus.Error, "API1-INVALID-TARGET", "API1 probe skipped", $"Could not parse target '{target}' as a URL.")];
        }
        if (!HasObjectId(uri))
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-NO-OBJECT-ID", "API1 BOLA needs an object-scoped target",
                "The target path doesn't end in an object id (numeric or UUID). Point --target at a specific object identity A owns (e.g. .../orders/1024) so B's access to it can be tested.")];
        }

        // Identity A must be able to read its own object — the baseline.
        var (statusA, bodyA) = await SafeGetAsync(http, target, authHeaders, ct).ConfigureAwait(false);
        if (statusA is < 200 or >= 300)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-NO-BASELINE", "No identity-A baseline",
                $"Identity A did not get a 2xx for {target} (HTTP {statusA}). Point --target at an object identity A can actually read.")];
        }

        // If anonymous can read it too, the object isn't access-controlled at
        // all — that's an authentication gap (API2), not object-level authz.
        var (statusAnon, _) = await SafeGetAsync(http, target, Array.Empty<string>(), ct).ConfigureAwait(false);
        if (statusAnon is >= 200 and < 300)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-PUBLIC-OBJECT", "Object is publicly readable",
                $"The object at {target} is readable with no credential (HTTP {statusAnon}) — it isn't access-controlled, so object-level authorization can't be assessed here (this is an API2 authentication concern).")];
        }

        // The actual BOLA test: can identity B read identity A's object?
        var (statusB, bodyB) = await SafeGetAsync(http, target, authHeadersB, ct).ConfigureAwait(false);
        if (statusB is >= 200 and < 300)
        {
            var exactMatch = bodyA.Length > 0 && string.Equals(bodyA, bodyB, StringComparison.Ordinal);
            if (exactMatch)
            {
                return [Finding("BWR-OWASP-API1-BOLA-EXACT", "Broken object-level authorization (identical object)",
                    $"Identity B read the exact same object as identity A at {target} (HTTP {statusB}), while anonymous access is blocked ({statusAnon}). A different authenticated identity can read another's object — object-level authorization is missing.",
                    "Enforce object ownership on every object access: check that the authenticated principal is authorized for the specific object id, not merely authenticated. Don't rely on unguessable ids.",
                    "critical", 9.1)];
            }
            return [Finding("BWR-OWASP-API1-BOLA", "Object readable by a second identity",
                $"Identity B received HTTP {statusB} for {target} while anonymous access is blocked ({statusAnon}). A second authenticated identity reached an auth-required object — verify it isn't an intentionally shared resource; otherwise object-level authorization is missing.",
                "Enforce object ownership on every object access: authorize the authenticated principal for the specific object id, not just that they're authenticated.",
                "high", 7.1)];
        }

        return [Marker(ScanFindingStatus.Safe, "API1-CLEAN", "Object-level authorization enforced",
            $"Identity B was denied the object (HTTP {statusB}) that identity A could read — object-level authorization is enforced for this object.")];
    }

    // ---- helpers ----

    private static bool SameIdentity(IList<string> a, IList<string> b)
        => string.Equals(
            string.Join("\n", a.Select(s => s.Trim())),
            string.Join("\n", b.Select(s => s.Trim())),
            StringComparison.Ordinal);

    private static bool HasObjectId(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        var last = segments[^1];
        return last.All(char.IsDigit) || Uuid().IsMatch(last);
    }

    private static async Task<(int Status, string Body)> SafeGetAsync(HttpClient http, string url, IList<string> headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ScanCommand.ApplyAuthHeaders(req, headers);
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
            remediation: "Diagnostic marker for the API1 suite row."),
        Status = status,
        Detail = detail,
    };
}

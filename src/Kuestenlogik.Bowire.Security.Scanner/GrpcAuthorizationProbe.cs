// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for gRPC, rolling up to <c>API5:2023 — Broken Function
/// Level Authorization</c>. Detects a server that <em>authenticates</em> a
/// caller and then lets them reach functions their identity does not entitle
/// them to.
///
/// <para>The sibling <see cref="GrpcReflectionProbe"/> asks whether a method
/// answers a caller with no credential at all. This one asks the question
/// that survives a yes to authentication, and it is a different failure:
/// Bowire.VulnDb#22 is the shape — a server that never bound the authenticated
/// principal to the executing thread, so every authenticated reader reached
/// functions reserved for administrators. Interceptor-style authentication
/// makes this easy to get wrong, because the interceptor proves a credential
/// exists and the handler then assumes someone downstream checked what it is
/// good for.</para>
///
/// <para>Three observations, the same way <see cref="Api1BolaProbe"/> and
/// <see cref="WebSocketAuthorizationProbe"/> reach a verdict: anonymous must
/// be refused (or the method is simply public, which is the other probe's
/// finding), identity A must reach the handler (or there is no baseline to
/// compare against), and identity B must then also reach it. Only that
/// combination shows a server gating on <em>having</em> a credential rather
/// than on <em>whose</em> it is.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and deliberately narrow.</b> The probe invokes only methods
/// whose names say both that they read and that they touch a management-plane
/// noun — <c>ListPolicies</c>, <c>GetAuditLog</c>, <c>ExportSecrets</c>. That
/// is not the whole of the class: VulnDb#22's own method was
/// <c>executeCommand</c>, which this will never call, because a probe that
/// invokes a privileged <em>mutating</em> method to see whether it is guarded
/// belongs in the active tier behind <c>--active</c> and an explicit warning,
/// not in a scan an operator expects to be inert. What remains — can a
/// lower-privileged identity read the management plane — is a large, real and
/// safely testable part of it.
/// </para>
/// <para>
/// On a server with reflection disabled the method list comes from
/// <c>--grpc-descriptor-set</c> (#653). Without either, the probe says so
/// rather than reporting a hardened server as clean.
/// </para>
/// </remarks>
internal sealed class GrpcAuthorizationProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API5:2023");

    public string ProtocolId => "grpc";

    /// <summary>
    /// Without a second identity there is nothing to compare, so the
    /// single-identity entry point stays silent rather than guessing.
    /// </summary>
    public Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScanFinding>>([]);

    /// <summary>
    /// Method-name prefixes that read. The probe will not invoke anything
    /// outside this set, so a mutating management call is out of reach even
    /// when it carries a privileged noun.
    /// </summary>
    private static readonly string[] s_readingPrefixes =
    [
        "Get", "List", "Describe", "Read", "Export", "Fetch", "Show", "Query", "Search", "Dump",
    ];

    /// <summary>
    /// Management-plane nouns. Kept tight on purpose: a generic
    /// <c>GetUser</c> is how an application serves a caller their own
    /// profile, and flagging it would bury the finding that matters under
    /// noise. Every entry here names something a reader has no business
    /// reaching in an application that separates roles at all.
    /// </summary>
    private static readonly string[] s_privilegedNouns =
    [
        "Admin", "Secret", "Credential", "Policy", "Policies", "Role", "Permission", "Grant",
        "Audit", "Backup", "License", "Tenant", "Users", "Accounts", "Members", "ApiKey", "ApiKeys",
        "Config", "Configuration", "Setting", "Settings", "Principal", "Privilege",
    ];

    /// <summary>
    /// gRPC status trailers proving the call was stopped before the handler.
    /// </summary>
    private static readonly HashSet<string> s_refused = new(StringComparer.Ordinal)
    {
        "Unauthenticated", "PermissionDenied",
    };

    /// <summary>
    /// Statuses that only arise once a call has reached the method body — so
    /// whatever gate exists, this caller passed it.
    /// </summary>
    private static readonly HashSet<string> s_reachedHandler = new(StringComparer.Ordinal)
    {
        "OK", "InvalidArgument", "NotFound", "AlreadyExists", "FailedPrecondition",
        "OutOfRange", "ResourceExhausted", "Aborted", "DataLoss", "Internal",
    };

    /// <summary>
    /// How many candidates to try before giving up. A method whose empty
    /// <c>{}</c> request cannot be marshalled yields no verdict, so the probe
    /// falls through to the next rather than calling the whole check
    /// inconclusive on the first awkward one.
    /// </summary>
    private const int MaxCandidates = 6;

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(OwaspProbeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var authHeaders = context.AuthHeaders;
        var authHeadersB = context.AuthHeadersB;

        if (authHeaders is null || authHeaders.Count == 0
            || authHeadersB is null || authHeadersB.Count == 0)
        {
            // Not a coverage gap — a cross-identity check without a second
            // identity has nothing to say, and saying it anyway would put a
            // note on every ordinary scan.
            return [];
        }

        if (SameIdentity(authHeaders, authHeadersB))
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-GRPC-SAME-IDENTITY",
                "gRPC authorization check skipped",
                "--auth-header and --auth-header-b carry the same credential, so B reaching what A reaches proves nothing. Supply a second, lower-privileged identity to run this check.")];
        }

        var services = await DiscoverAsync(context, ct).ConfigureAwait(false);
        if (services.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-GRPC-NO-METHODS",
                "gRPC authorization check skipped",
                "No gRPC services could be enumerated: the target is not a gRPC endpoint, or Server Reflection is disabled (the desired production state). "
                + "Re-run with --grpc-descriptor-set <api.protoset> so the check can learn which methods to test.")];
        }

        var candidates = FindPrivilegedReadOnlyMethods(services);
        if (candidates.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-GRPC-NO-CANDIDATE",
                "gRPC authorization check skipped",
                "No method was both read-only and management-plane by name (a ListPolicies / GetAuditLog / ExportSecrets shape), so there was nothing this check could invoke without risking a side effect. "
                + "A privileged *mutating* method is deliberately out of scope here — that belongs to the active tier behind --active.")];
        }

        var metaA = ToMetadata(authHeaders);
        var metaB = ToMetadata(authHeadersB);
        string? lastSkipped = null;

        foreach (var (service, method) in candidates.Take(MaxCandidates))
        {
            var full = $"{service}/{method}";

            // 1. Anonymous must be refused. If it is not, the method is
            //    public and GrpcReflectionProbe reports that — this probe
            //    staying quiet avoids two findings for one hole, and a
            //    "B got in" claim that would be true of any caller at all.
            var anonymous = await CallAsync(context, service, method, metadata: null, ct).ConfigureAwait(false);
            if (anonymous is null) { lastSkipped = full; continue; }
            if (s_reachedHandler.Contains(anonymous)) { lastSkipped = full; continue; }
            if (!s_refused.Contains(anonymous)) { lastSkipped = full; continue; }

            // 2. Identity A must reach the handler, or there is no baseline:
            //    B being refused would say nothing if A is refused too.
            var asA = await CallAsync(context, service, method, metaA, ct).ConfigureAwait(false);
            if (asA is null || !s_reachedHandler.Contains(asA)) { lastSkipped = full; continue; }

            // 3. B in the same place. Reaching the handler means the server
            //    checked that a credential exists and never checked whose.
            var asB = await CallAsync(context, service, method, metaB, ct).ConfigureAwait(false);
            if (asB is null) { lastSkipped = full; continue; }

            if (s_refused.Contains(asB))
            {
                return [Marker(ScanFindingStatus.Safe, "API5-GRPC-AUTHZ-ENFORCED",
                    "gRPC function-level authorization enforced",
                    $"An anonymous call to {full} was refused, identity A reached the handler, and identity B was refused with {asB} — the server distinguishes between authenticated callers on a management-plane method rather than merely requiring one.")];
            }

            if (s_reachedHandler.Contains(asB))
            {
                return [Finding("BWR-OWASP-API5-GRPC-NOAUTHZ",
                    "gRPC authenticates the caller but does not authorize them",
                    $"An anonymous call to {full} was refused ({anonymous}), and calls carrying two different identities both reached the method body (A: {asA}, B: {asB}). "
                    + "The server therefore checks that a credential is present and not what it entitles the holder to — every authenticated account reaches this management-plane method, whatever the permission model says elsewhere. "
                    + "This is the failure that survives every \"is it public?\" check: the method looks protected, and is, against strangers only.",
                    "Authorize each method, don't only authenticate the connection. An interceptor that validates a token proves a caller exists; it does not decide what they may call. "
                    + "Bind the authenticated principal to the call's execution context and consult the same role or permission model the rest of the application enforces, inside the handler or in a per-method authorization interceptor. "
                    + "Where a framework hands the principal to the handler through ambient state, verify it is actually populated on the executing thread — a principal that never arrives makes every check silently pass.",
                    "high", 8.1)];
            }

            lastSkipped = full;
        }

        return [Marker(ScanFindingStatus.Skipped, "API5-GRPC-INCONCLUSIVE",
            "gRPC authorization check inconclusive",
            $"{candidates.Count} management-plane method(s) were tried"
            + (lastSkipped is null ? "" : $" (last: {lastSkipped})")
            + ", and none produced the three observations this verdict needs — an anonymous refusal, then identity A reaching the handler. "
            + "Either the methods reject an empty {} request before any authorization runs, or A is not entitled to them.")];
    }

    /// <summary>
    /// Services from reflection, falling back to a supplied descriptor set.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GrpcReflectionProbe"/>, the two sources are
    /// interchangeable here: this probe never reports on what the server
    /// discloses, only on what it lets identities do, so a method's origin
    /// changes nothing about the verdict.
    /// </remarks>
    private static async Task<List<BowireServiceInfo>> DiscoverAsync(OwaspProbeContext context, CancellationToken ct)
    {
        try
        {
            var reflected = await context.Protocol
                .DiscoverAsync(context.Target, showInternalServices: false, ct).ConfigureAwait(false);
            if (reflected.Count > 0) return reflected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Fall through to the supplied set — a server that refuses to
            // enumerate itself is the case the set exists for.
        }

        var metadata = context.ProtocolMetadata;
        if (metadata is null || !metadata.ContainsKey(BowireMetadataKeys.GrpcDescriptorSet)) return [];

        try
        {
            return await context.Protocol
                .DiscoverAsync(context.Target, showInternalServices: false, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// One call with an empty request, returning the gRPC status or
    /// <c>null</c> when the call could not be made at all.
    /// </summary>
    /// <remarks>
    /// A descriptor set travels alongside the credential, because the plugin
    /// needs it to marshal the request at all; it is configuration and never
    /// reaches the wire as a header.
    /// </remarks>
    private static async Task<string?> CallAsync(
        OwaspProbeContext context, string service, string method,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var merged = WithDescriptorSet(metadata, context.ProtocolMetadata);
        try
        {
            var result = await context.Protocol.InvokeAsync(
                context.Target, service, method, ["{}"],
                showInternalServices: false, metadata: merged, ct).ConfigureAwait(false);
            return result.Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? WithDescriptorSet(
        Dictionary<string, string>? credential, IReadOnlyDictionary<string, string>? protocolMetadata)
    {
        if (protocolMetadata is null || protocolMetadata.Count == 0) return credential;

        var merged = credential is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(credential, StringComparer.Ordinal);
        foreach (var (k, v) in protocolMetadata) merged[k] = v;
        return merged;
    }

    /// <summary>
    /// Unary methods whose name says they read <em>and</em> that they touch
    /// the management plane.
    /// </summary>
    private static List<(string Service, string Method)> FindPrivilegedReadOnlyMethods(
        List<BowireServiceInfo> services)
    {
        var found = new List<(string, string)>();
        foreach (var svc in services)
        {
            foreach (var m in svc.Methods)
            {
                if (m.ClientStreaming || m.ServerStreaming) continue;
                if (!s_readingPrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal))) continue;
                if (!s_privilegedNouns.Any(n => m.Name.Contains(n, StringComparison.Ordinal))) continue;
                found.Add((svc.Name, m.Name));
            }
        }
        return found;
    }

    /// <summary>
    /// Whether two identities are the same credential, ignoring header order
    /// and case in the names.
    /// </summary>
    private static bool SameIdentity(IList<string> a, IList<string> b)
    {
        var setA = a.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).OrderBy(h => h, StringComparer.Ordinal);
        var setB = b.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).OrderBy(h => h, StringComparer.Ordinal);
        return setA.SequenceEqual(setB, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fold the scan's auth-header values (<c>Name: Value</c> strings) into
    /// the metadata bag the plugin forwards as gRPC request headers.
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
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-285", owaspApi: Entry.Tag,
            severity: severity, cvss: cvss, remediation: remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string suffix, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build($"BWR-OWASP-{suffix}", name,
            cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the gRPC function-level authorization check."),
        Status = status,
        Detail = detail,
    };
}

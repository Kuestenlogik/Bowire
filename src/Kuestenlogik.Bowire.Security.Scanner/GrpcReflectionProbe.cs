// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for gRPC. Two checks, driven through the gRPC plugin's own
/// invoke path:
/// <list type="number">
///   <item><b>Server reflection exposure → API9.</b> Queries gRPC Server
///   Reflection <em>anonymously</em>; services coming back mean reflection is
///   publicly enabled — the gRPC analog of an exposed API inventory (any
///   client can enumerate every service, method, and message schema without a
///   <c>.proto</c>).</item>
///   <item><b>Missing transport authentication → API2.</b> When
///   <c>--auth-header</c> asserts that the API expects a credential, the probe
///   invokes one read-only, unary, reflection-discovered method <em>without</em>
///   that credential and reads the gRPC status trailer: an <c>Unauthenticated</c>
///   / <c>PermissionDenied</c> trailer means auth is enforced; any status that
///   shows the call reached the handler means it wasn't.</item>
/// </list>
/// The reflection check is discovery-only. The auth check invokes exactly one
/// method, gated to a read-only name so it can't trip a mutating business
/// flow, and only when reflection already surfaced a method to test.
/// </summary>
internal sealed class GrpcReflectionProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API9:2023");

    private static readonly OwaspApiEntry s_api2 = OwaspApiCatalog.Entries.Single(e => e.Id == "API2:2023");

    public string ProtocolId => "grpc";

    // Method-name prefixes that are read-only by convention — the auth check
    // only ever invokes one of these so it can't trigger a mutating flow.
    private static readonly string[] s_readOnlyPrefixes =
    [
        "Get", "List", "Query", "Describe", "Fetch", "Read", "Search", "Lookup",
        "Show", "Ping", "Check", "Stat", "Info", "Version", "Index", "Health", "Status",
    ];

    // gRPC status trailers proving auth was enforced before the handler ran.
    private static readonly HashSet<string> s_enforced = new(StringComparer.Ordinal)
    {
        "Unauthenticated", "PermissionDenied",
    };

    // gRPC statuses that only arise once a call has passed the transport and
    // reached the method body — i.e. no credential was required to get there.
    private static readonly HashSet<string> s_reachedHandler = new(StringComparer.Ordinal)
    {
        "OK", "InvalidArgument", "NotFound", "AlreadyExists", "FailedPrecondition",
        "OutOfRange", "ResourceExhausted", "Aborted", "DataLoss", "Internal",
    };

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        List<BowireServiceInfo> services;
        try
        {
            // DiscoverAsync uses the ServerReflection API with no credentials —
            // this measures anonymous reflection exposure, not authorised use.
            services = await protocol.DiscoverAsync(target, showInternalServices: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(Entry, ScanFindingStatus.Skipped, "API9-GRPC-UNREACHABLE", "gRPC reflection probe skipped",
                $"Anonymous gRPC Server Reflection could not be attempted ({ex.GetType().Name}).")];
        }

        var serviceCount = services.Count;
        var methodCount = services.Sum(s => s.Methods.Count);
        if (serviceCount == 0)
        {
            return [Marker(Entry, ScanFindingStatus.Skipped, "API9-GRPC-NO-REFLECTION", "gRPC reflection not exposed",
                "Anonymous gRPC Server Reflection returned no services — the target is not a gRPC endpoint, or reflection is disabled (the desired production state).")];
        }

        var findings = new List<ScanFinding>();

        var names = string.Join(", ", services.Take(5).Select(s => s.Name));
        var ellipsis = serviceCount > 5 ? ", …" : "";
        findings.Add(Finding("BWR-OWASP-API9-GRPC-REFLECTION", "gRPC server reflection enabled", Entry.Tag, "CWE-200",
            $"Anonymous gRPC Server Reflection returned {serviceCount} service(s) / {methodCount} method(s) ({names}{ellipsis}). Public reflection lets any client enumerate every service, method, and message schema — the gRPC analog of an exposed API inventory.",
            "Disable gRPC Server Reflection in production, or gate it behind auth — it is a debugging aid. Grpc.AspNetCore: don't register AddGrpcReflection / MapGrpcReflectionService in prod; ship .proto files to legitimate consumers out-of-band instead.",
            "medium", 5.3));

        // Auth check (API2) — only meaningful when a credential is expected.
        // Without --auth-header we can't tell an intentionally-public gRPC API
        // from a broken one, so we leave API2 to the caller's other probes.
        if (authHeaders.Count > 0)
        {
            findings.Add(await CheckTransportAuthAsync(target, protocol, services, ct).ConfigureAwait(false));
        }

        return findings;
    }

    /// <summary>
    /// Number of read-only candidate methods the auth check will try before
    /// giving up. A method whose empty <c>{}</c> request can't be marshalled
    /// yields no verdict, so we fall through to the next candidate rather than
    /// reporting the whole check inconclusive on the first awkward method.
    /// </summary>
    private const int MaxAuthCandidates = 6;

    /// <summary>
    /// Invoke read-only, unary, reflection-discovered methods with no
    /// credential and classify the gRPC status trailer. Tries several
    /// candidates until one yields an accept/reject verdict.
    /// </summary>
    private static async Task<ScanFinding> CheckTransportAuthAsync(string target, IBowireProtocol protocol, List<BowireServiceInfo> services, CancellationToken ct)
    {
        var candidates = FindReadOnlyUnaryMethods(services);
        if (candidates.Count == 0)
        {
            return Marker(s_api2, ScanFindingStatus.Skipped, "API2-GRPC-NO-READONLY", "gRPC auth check skipped",
                "Reflection surfaced no read-only, unary method safe to invoke without side effects — the transport-auth check needs one (a Get* / List* / Health* … method) to probe anonymously.");
        }

        string? lastStatus = null;
        string? lastMethod = null;
        foreach (var (service, method) in candidates.Take(MaxAuthCandidates))
        {
            string status;
            try
            {
                // No metadata → no credential. If the server enforces auth at
                // the transport, this call is rejected before the handler runs.
                var result = await protocol.InvokeAsync(target, service, method, ["{}"],
                    showInternalServices: false, metadata: null, ct).ConfigureAwait(false);
                status = result.Status;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue; // this method couldn't be invoked cleanly — try the next
            }

            if (s_enforced.Contains(status))
            {
                return Marker(s_api2, ScanFindingStatus.Safe, "API2-GRPC-AUTH-ENFORCED", "gRPC transport auth enforced",
                    $"An anonymous call to {service}/{method} was rejected with gRPC status {status} — the method enforces authentication before the handler runs.");
            }

            if (s_reachedHandler.Contains(status))
            {
                return new ScanFinding
                {
                    Template = SyntheticTemplate.Build("BWR-OWASP-API2-GRPC-NOAUTH", "gRPC method reachable without authentication",
                        cwe: "CWE-306", owaspApi: s_api2.Tag, severity: "high", cvss: 7.5,
                        remediation: "Enforce authentication at the transport for every gRPC method — a server interceptor / metadata credential check that rejects missing or invalid tokens with UNAUTHENTICATED before the handler runs. Don't rely on per-handler checks that a new method can forget."),
                    Status = ScanFindingStatus.Vulnerable,
                    Detail = $"An anonymous call to {service}/{method} (no credential, despite --auth-header being supplied) returned gRPC status {status} — the request reached the method body without authentication. Any client can invoke it without a token.",
                };
            }

            lastStatus = status;
            lastMethod = $"{service}/{method}";
        }

        return Marker(s_api2, ScanFindingStatus.Skipped, "API2-GRPC-INCONCLUSIVE", "gRPC auth check inconclusive",
            lastStatus is null
                ? "No read-only method could be invoked cleanly with an empty request — transport-auth enforcement not determined."
                : $"Anonymous calls returned non-verdict gRPC statuses (last: {lastMethod} → {lastStatus}) — inconclusive for auth enforcement (transport / availability rather than accept/reject).");
    }

    // Read-only unary candidates, interleaved round-robin across services so
    // one service with many (possibly un-marshallable) methods can't crowd out
    // a simpler method on another service before the candidate cap is hit.
    private static List<(string Service, string Method)> FindReadOnlyUnaryMethods(List<BowireServiceInfo> services)
    {
        var perService = new List<List<(string, string)>>();
        foreach (var service in services)
        {
            var methods = new List<(string, string)>();
            foreach (var method in service.Methods)
            {
                if (method.ClientStreaming || method.ServerStreaming) continue;
                if (s_readOnlyPrefixes.Any(p => method.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    methods.Add((service.Name, method.Name));
            }
            if (methods.Count > 0) perService.Add(methods);
        }

        var found = new List<(string, string)>();
        var maxDepth = perService.Count == 0 ? 0 : perService.Max(m => m.Count);
        for (var depth = 0; depth < maxDepth; depth++)
        {
            foreach (var methods in perService)
            {
                if (depth < methods.Count) found.Add(methods[depth]);
            }
        }
        return found;
    }

    // ---- finding factories ----

    private static ScanFinding Finding(string id, string name, string owaspApi, string cwe, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: cwe, owaspApi: owaspApi, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private static ScanFinding Marker(OwaspApiEntry entry, ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the gRPC protocol probe."),
        Status = status,
        Detail = detail,
    };
}

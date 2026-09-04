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
        => await RunAsync(new OwaspProbeContext
        {
            Target = target,
            Protocol = protocol,
            AuthHeaders = authHeaders,
        }, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(OwaspProbeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Target;
        var protocol = context.Protocol;

        List<BowireServiceInfo> reflected;
        try
        {
            // No metadata, deliberately: this call measures what the server
            // hands an anonymous stranger. Passing the operator's descriptor
            // set here would answer the question out of their own file and
            // report services as publicly enumerable that this server never
            // disclosed.
            reflected = await protocol.DiscoverAsync(target, showInternalServices: false, ct).ConfigureAwait(false);
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

        // A descriptor set the operator supplied (--grpc-descriptor-set), if
        // any. Kept strictly apart from what reflection disclosed: this list is
        // what we may *call*, never evidence of what the server *exposes*.
        var supplied = await TrySuppliedServicesAsync(context, ct).ConfigureAwait(false);

        var findings = new List<ScanFinding>();

        if (reflected.Count == 0 && supplied.Count == 0)
        {
            // Two facts, and the second one used to be missing. Reporting
            // only "reflection is off, which is what you want" reads as
            // reassurance about a server whose authentication was never
            // examined — and the better a deployment follows this probe's
            // own first recommendation, the more often that happens (#652).
            return [Marker(Entry, ScanFindingStatus.Skipped, "API9-GRPC-NO-REFLECTION", "gRPC reflection not exposed — and the auth check could not run",
                "Anonymous gRPC Server Reflection returned no services: the target is not a gRPC endpoint, or reflection is disabled (the desired production state). "
                + "Either way the transport-authentication check did NOT run — it needs a method to call, and it had no other way to learn of one. "
                + "Re-run with --grpc-descriptor-set <api.protoset> to name the methods yourself and let the check proceed.")];
        }

        if (reflected.Count > 0)
        {
            var names = string.Join(", ", reflected.Take(5).Select(s => s.Name));
            var ellipsis = reflected.Count > 5 ? ", …" : "";
            var methodCount = reflected.Sum(s => s.Methods.Count);
            findings.Add(Finding("BWR-OWASP-API9-GRPC-REFLECTION", "gRPC server reflection enabled", Entry.Tag, "CWE-200",
                $"Anonymous gRPC Server Reflection returned {reflected.Count} service(s) / {methodCount} method(s) ({names}{ellipsis}). Public reflection lets any client enumerate every service, method, and message schema — the gRPC analog of an exposed API inventory.",
                "Disable gRPC Server Reflection in production, or gate it behind auth — it is a debugging aid. Grpc.AspNetCore: don't register AddGrpcReflection / MapGrpcReflectionService in prod; ship .proto files to legitimate consumers out-of-band instead.",
                "medium", 5.3));
        }
        else
        {
            // The case #652 was filed for, now answered rather than reported:
            // reflection is off *and* the auth check can still run.
            findings.Add(Marker(Entry, ScanFindingStatus.Safe, "API9-GRPC-NO-REFLECTION", "gRPC reflection not exposed",
                $"Anonymous gRPC Server Reflection returned no services — the desired production state. The {supplied.Count} service(s) named by --grpc-descriptor-set are used below to test authentication; they are the operator's own declaration and say nothing about what this server discloses."));
        }

        // Prefer what the server admitted to: it is ground truth about this
        // deployment, where a supplied set may describe methods it does not host.
        var callable = reflected.Count > 0 ? reflected : supplied;

        // Auth check (API2) — only meaningful when a credential is expected.
        // Without --auth-header we can't tell an intentionally-public gRPC API
        // from a broken one, so we leave API2 to the caller's other probes.
        if (context.AuthHeaders.Count > 0)
        {
            findings.Add(await CheckTransportAuthAsync(
                target, protocol, callable, context.ProtocolMetadata,
                fromSuppliedSet: reflected.Count == 0, ct).ConfigureAwait(false));
        }

        return findings;
    }

    /// <summary>
    /// The services named by a caller-supplied descriptor set, or empty when
    /// the scan supplied none.
    /// </summary>
    /// <remarks>
    /// A set that cannot be read is not worth failing the probe over — the
    /// reflection half has already run and has something to say — so it
    /// degrades to the same message a scan with no set at all prints.
    /// </remarks>
    private static async Task<List<BowireServiceInfo>> TrySuppliedServicesAsync(
        OwaspProbeContext context, CancellationToken ct)
    {
        var metadata = context.ProtocolMetadata;
        if (metadata is null || !metadata.ContainsKey(BowireMetadataKeys.GrpcDescriptorSet)) return [];

        try
        {
            return await context.Protocol
                .DiscoverAsync(context.Target, showInternalServices: false, metadata, ct)
                .ConfigureAwait(false);
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
    /// Number of read-only candidate methods the auth check will try before
    /// giving up. A method whose empty <c>{}</c> request can't be marshalled
    /// yields no verdict, so we fall through to the next candidate rather than
    /// reporting the whole check inconclusive on the first awkward method.
    /// </summary>
    private const int MaxAuthCandidates = 6;

    /// <summary>
    /// Invoke read-only, unary methods with no credential and classify the
    /// gRPC status trailer. Tries several candidates until one yields an
    /// accept/reject verdict.
    /// </summary>
    /// <param name="target">The URL this attempt is against.</param>
    /// <param name="protocol">The resolved gRPC plugin.</param>
    /// <param name="services">The methods available to try, from whichever source named them.</param>
    /// <param name="protocolMetadata">
    /// Plugin configuration — a descriptor set, when one was supplied. It is
    /// not a credential: the plugin strips these markers before anything
    /// reaches the wire, so the call stays anonymous, which is the entire
    /// point of it.
    /// </param>
    /// <param name="fromSuppliedSet">
    /// Whether <paramref name="services"/> came from the operator's descriptor
    /// set rather than from reflection. Only changes how the verdict is
    /// worded — a method named by a set may not be hosted at all, and the
    /// reader should be told which of the two they are looking at.
    /// </param>
    /// <param name="ct">Cancellation for the whole check.</param>
    private static async Task<ScanFinding> CheckTransportAuthAsync(
        string target, IBowireProtocol protocol, List<BowireServiceInfo> services,
        IReadOnlyDictionary<string, string>? protocolMetadata, bool fromSuppliedSet, CancellationToken ct)
    {
        var source = fromSuppliedSet ? "--grpc-descriptor-set" : "Reflection";
        var candidates = FindReadOnlyUnaryMethods(services);
        if (candidates.Count == 0)
        {
            return Marker(s_api2, ScanFindingStatus.Skipped, "API2-GRPC-NO-READONLY", "gRPC auth check skipped",
                $"{source} surfaced no read-only, unary method safe to invoke without side effects — the transport-auth check needs one (a Get* / List* / Health* … method) to probe anonymously.");
        }

        // The marker travels; nothing else does. Converted here because
        // InvokeAsync takes a mutable bag it does not mutate.
        var callMetadata = protocolMetadata is null || protocolMetadata.Count == 0
            ? null
            : protocolMetadata.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        string? lastStatus = null;
        string? lastMethod = null;
        foreach (var (service, method) in candidates.Take(MaxAuthCandidates))
        {
            string status;
            try
            {
                // No credential. If the server enforces auth at the transport,
                // this call is rejected before the handler runs.
                var result = await protocol.InvokeAsync(target, service, method, ["{}"],
                    showInternalServices: false, metadata: callMetadata, ct).ConfigureAwait(false);
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
                    Detail = $"An anonymous call to {service}/{method} (no credential, despite --auth-header being supplied) returned gRPC status {status} — the request reached the method body without authentication. Any client can invoke it without a token. The method was named by {source}.",
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

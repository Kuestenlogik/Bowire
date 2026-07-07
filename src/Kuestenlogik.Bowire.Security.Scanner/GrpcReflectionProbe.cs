// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for gRPC, rolling up to <c>API9:2023 — Improper Inventory
/// Management</c>. It drives the gRPC plugin's discovery path, which queries
/// gRPC Server Reflection <em>anonymously</em>. Services coming back mean
/// reflection is publicly enabled — the gRPC analog of an exposed API
/// inventory: any client can enumerate every service, method, and message
/// schema without a single <c>.proto</c> file.
///
/// <para>Discovery-only, so it never invokes a method or mutates the target.
/// When reflection is off (the desired production state) or the target does
/// not speak gRPC, the probe skips rather than reporting a false pass.</para>
/// </summary>
internal sealed class GrpcReflectionProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API9:2023");

    public string ProtocolId => "grpc";

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
            return [Marker(ScanFindingStatus.Skipped, "API9-GRPC-UNREACHABLE", "gRPC reflection probe skipped",
                $"Anonymous gRPC Server Reflection could not be attempted ({ex.GetType().Name}).")];
        }

        var serviceCount = services.Count;
        var methodCount = services.Sum(s => s.Methods.Count);
        if (serviceCount == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-GRPC-NO-REFLECTION", "gRPC reflection not exposed",
                "Anonymous gRPC Server Reflection returned no services — the target is not a gRPC endpoint, or reflection is disabled (the desired production state).")];
        }

        var names = string.Join(", ", services.Take(5).Select(s => s.Name));
        var ellipsis = serviceCount > 5 ? ", …" : "";
        return [Finding("BWR-OWASP-API9-GRPC-REFLECTION", "gRPC server reflection enabled",
            $"Anonymous gRPC Server Reflection returned {serviceCount} service(s) / {methodCount} method(s) ({names}{ellipsis}). Public reflection lets any client enumerate every service, method, and message schema — the gRPC analog of an exposed API inventory.",
            "Disable gRPC Server Reflection in production, or gate it behind auth — it is a debugging aid. Grpc.AspNetCore: don't register AddGrpcReflection / MapGrpcReflectionService in prod; ship .proto files to legitimate consumers out-of-band instead.",
            "medium", 5.3)];
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-200", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the gRPC reflection probe."),
        Status = status,
        Detail = detail,
    };
}

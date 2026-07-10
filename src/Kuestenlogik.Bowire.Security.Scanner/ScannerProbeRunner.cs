// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Scanner-backed <see cref="ISecurityScanProbeRunner"/> (#104): the live
/// probe-execution stage for the AI scan orchestration. Runs the HTTP-class
/// OWASP probes + built-in passive checks against the endpoint's URL and maps
/// the vulnerable findings into <see cref="OrchestratedFinding"/>s the
/// orchestrator triages. Best-effort — a probe failure yields no findings for
/// that endpoint rather than aborting the whole pipeline.
/// </summary>
public sealed class ScannerProbeRunner : ISecurityScanProbeRunner
{
    public async Task<IReadOnlyList<OrchestratedFinding>> RunAsync(OrchestratorEndpoint endpoint, string target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var url = CombineUrl(target, endpoint.Path);
        if (url is null) return [];

        using var http = BuildHttp();
        var findings = new List<ScanFinding>();
        try
        {
            findings.AddRange(await OwaspApiSuite.RunProbesAsync(url, http, [], [], ct).ConfigureAwait(false));
            findings.AddRange(await SecurityBuiltins.RunAllAsync(url, http, [], ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return []; // best-effort — don't let one endpoint's probe failure sink the run
        }

        return findings
            .Where(f => f.Status == ScanFindingStatus.Vulnerable)
            .Select(f =>
            {
                var vuln = f.Template.Recording.Vulnerability;
                var ruleId = string.IsNullOrEmpty(vuln?.Id) ? "unknown" : vuln!.Id;
                var title = string.IsNullOrEmpty(f.Template.Recording.Name) ? ruleId : f.Template.Recording.Name;
                return new OrchestratedFinding(endpoint.EndpointId, ruleId, title, vuln?.Severity ?? "medium", vuln?.OwaspApi);
            })
            .ToArray();
    }

    private static HttpClient BuildHttp()
    {
        HttpClientHandler? handler = null;
        try
        {
            handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
            handler = null; // ownership transferred to the HttpClient (disposeHandler: true)
            return client;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    // Resolve the endpoint URL: an absolute http(s) path wins; otherwise join the
    // (relative) path onto the target base.
    private static string? CombineUrl(string target, string path)
    {
        path ??= "";
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.ToString();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var baseUri)) return null;
        var rel = path.StartsWith('/') ? path[1..] : path;
        return Uri.TryCreate(baseUri, rel, out var joined) ? joined.ToString() : baseUri.ToString();
    }
}

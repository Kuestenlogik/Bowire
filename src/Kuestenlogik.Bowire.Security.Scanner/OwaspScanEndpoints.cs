// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Workbench-side surface for the OWASP API Top 10 suite — auto-discovered by
/// Core at <c>MapBowire()</c> time via <see cref="IBowireEndpointContribution"/>.
/// Lets the Security rail render the ten entries and run the suite against a
/// target without shelling out to <c>bowire scan</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>GET  {base}/api/security/owasp-catalog</c> — the ten entries as
///   static metadata (id / title / reference), so the rail can list them even
///   before a scan.</item>
///   <item><c>POST {base}/api/security/owasp-scan</c> — runs the built-in
///   passive checks + the dedicated per-entry probes against a target and
///   returns the per-entry roll-up (status + findings).</item>
/// </list>
/// </remarks>
public sealed class OwaspScanEndpoints : IBowireEndpointContribution
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CRL is set explicitly below based on the caller's allowSelfSignedCerts choice, mirroring the fuzz endpoint.")]
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/security/owasp-catalog", () =>
            Results.Json(OwaspApiCatalog.Entries.Select(e => new
            {
                id = e.Id,
                title = e.Title,
                reference = e.Reference,
            }).ToArray())).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/security/owasp-scan", async (HttpContext ctx) =>
        {
            OwaspScanRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<OwaspScanRequest>(ctx.Request.Body, s_jsonOpts, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.Problem(title: "Request body isn't valid JSON", detail: ex.Message, statusCode: 400);
            }
            if (req is null || string.IsNullOrWhiteSpace(req.Target))
                return Results.Problem(title: "'target' is required", statusCode: 400);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (req.AllowSelfSignedCerts) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            else handler.CheckCertificateRevocationList = true;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(req.TimeoutSeconds > 0 ? req.TimeoutSeconds : 30) };

            var authA = (IList<string>)(req.AuthHeaders ?? []);
            var authB = (IList<string>)(req.AuthHeadersB ?? []);

            var findings = new List<ScanFinding>();
            try
            {
                if (req.RunBuiltins)
                    findings.AddRange(await SecurityBuiltins.RunAllAsync(req.Target, http, authA, ctx.RequestAborted).ConfigureAwait(false));
                findings.AddRange(await OwaspApiSuite.RunProbesAsync(req.Target, http, authA, authB, ctx.RequestAborted).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException or IOException)
            {
                return Results.Problem(title: "OWASP scan failed", detail: ex.Message, statusCode: 500);
            }

            var rollup = OwaspApiSuite.Rollup(findings);
            var byEntry = findings
                .Select(f => (Entry: OwaspApiCatalog.Match(f.Template.Recording.Vulnerability?.OwaspApi), Finding: f))
                .Where(x => x.Entry is not null)
                .GroupBy(x => x.Entry!.Id)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Finding).ToList(), StringComparer.Ordinal);

            var entries = rollup.Select(r => new
            {
                id = r.Entry.Id,
                title = r.Entry.Title,
                reference = r.Entry.Reference,
                status = r.Status.ToString(),
                vulnCount = r.VulnCount,
                findings = (byEntry.TryGetValue(r.Entry.Id, out var fs) ? fs : [])
                    .Select(f => new
                    {
                        id = f.Template.Recording.Vulnerability?.Id,
                        name = f.Template.Recording.Name,
                        severity = f.Template.Recording.Vulnerability?.Severity,
                        status = f.Status.ToString(),
                        detail = f.Detail,
                    }).ToArray(),
            }).ToArray();

            return Results.Json(new
            {
                target = req.Target,
                covered = rollup.Count(r => r.Status is not OwaspEntryStatus.NotCovered),
                vulnerable = rollup.Count(r => r.Status is OwaspEntryStatus.Vulnerable),
                total = OwaspApiCatalog.Entries.Count,
                entries,
            });
        }).ExcludeFromDescription();
    }

    private sealed class OwaspScanRequest
    {
        public string Target { get; init; } = "";
        public string[]? AuthHeaders { get; init; }
        public string[]? AuthHeadersB { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
        public bool AllowSelfSignedCerts { get; init; }
        public bool RunBuiltins { get; init; } = true;
    }
}

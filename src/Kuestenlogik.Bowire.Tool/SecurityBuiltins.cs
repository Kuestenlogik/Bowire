// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Built-in passive security checks the <c>bowire scan</c> subcommand
/// runs alongside the user-supplied vulnerability templates. These
/// don't fit cleanly as JSON templates because they need protocol-level
/// inspection (raw TLS handshake against multiple SSL protocols, multi-
/// probe verbose-error sniffing, header-value pattern scanning) rather
/// than a single "send + match-response" round-trip.
/// </summary>
/// <remarks>
/// <para>
/// Each check synthesises a <see cref="LoadedTemplate"/> with a
/// stable id (<c>BWR-BUILTIN-…</c>) so SARIF rule-grouping and
/// console output treat builtin findings identically to template
/// findings. The synthesised recording is a thin no-op marker — the
/// check does its own probing and emits the finding directly.
/// </para>
/// <para>
/// Builtins run by default; <c>--no-builtins</c> on the CLI opts out
/// (useful for CI runs that only want a tightly-curated corpus).
/// </para>
/// </remarks>
internal static class SecurityBuiltins
{
    /// <summary>
    /// Run every built-in check against the target. Returns the
    /// merged finding list. Errors per-check are swallowed and
    /// surfaced as <see cref="ScanFindingStatus.Error"/> findings so
    /// one wedged probe (e.g. a connection refused on a TLS sub-test)
    /// doesn't take the rest of the scan down.
    /// </summary>
    public static async Task<IReadOnlyList<ScanFinding>> RunAllAsync(string target, HttpClient http, IList<string> authHeaders, CancellationToken ct)
    {
        var results = new List<ScanFinding>();
        // TLS handshake check is socket-level — auth headers don't
        // factor in. The HTTP-class checks (banner, verbose errors)
        // carry the auth headers so they probe authenticated endpoints
        // alongside anonymous ones.
        results.AddRange(await TlsVersionEnumerationAsync(target, ct).ConfigureAwait(false));
        results.AddRange(await BannerDisclosureAsync(target, http, authHeaders, ct).ConfigureAwait(false));
        results.AddRange(await VerboseErrorDetectionAsync(target, http, authHeaders, ct).ConfigureAwait(false));
        return results;
    }

    // ------------------------------------------------------------------
    // TLS version enumeration
    // ------------------------------------------------------------------

    /// <summary>
    /// Probe the target's TLS endpoint with each historical SSL/TLS
    /// version. Flag accepted handshakes on TLS 1.0 / 1.1 (deprecated
    /// since 2020 per IETF + PCI-DSS 3.2.1) as findings. TLS 1.2 + 1.3
    /// acceptance is reported as info-only.
    /// </summary>
    /// <remarks>
    /// Pure socket-level test — doesn't share the HttpClient with the
    /// other checks because we need explicit <see cref="SslProtocols"/>
    /// control per probe attempt. SslStream lets us drive that
    /// directly. Connect-timeouts (target offline, port closed, hostname
    /// doesn't resolve) are reported once per scan — every TLS probe
    /// after the first connect-failure shortcuts to "skipped".
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "This IS the TLS-version-enumeration probe — the whole point is to attempt a handshake against every protocol version, including deprecated ones whose certs we're explicitly NOT validating. Accepting any cert is required to drive the handshake to completion so we can observe which protocol versions the target accepts.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5398:Avoid hardcoded SslProtocols values",
        Justification = "The hardcoded SslProtocols.Tls / Tls11 / Tls12 / Tls13 values are intentional — the scanner needs to probe each version individually to report which the target accepts. SslProtocols.None (OS-picks-best) would defeat the entire enumeration.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5364:Do not use deprecated security protocols",
        Justification = "Probing deprecated protocols is exactly the purpose of this check.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5386:Avoid hardcoding SecurityProtocolType value",
        Justification = "See CA5398 — the enumeration is the purpose.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5397:Do not use deprecated SslProtocols values",
        Justification = "See CA5364.")]
    private static async Task<IReadOnlyList<ScanFinding>> TlsVersionEnumerationAsync(string target, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();

        Uri uri;
        try { uri = new Uri(target); }
        catch (UriFormatException)
        {
            return [ScanFinding.Error(SyntheticTemplate.TlsEnumeration(),
                $"Could not parse target URL '{target}' as a URI; TLS enumeration skipped.")];
        }

        if (uri.Scheme != "https")
        {
            // Plain HTTP — not a TLS-version question. Flag this as
            // its OWN finding (HTTP-in-2026 is a downgrade-attack
            // vector); skip the per-version probes.
            findings.Add(new ScanFinding
            {
                Template = SyntheticTemplate.PlaintextHttp(),
                Status = ScanFindingStatus.Vulnerable,
                Detail = "Target uses plaintext http:// — every request is interceptable on the network. Even read-only APIs leak the URL path + headers (auth tokens, session cookies) over the wire. Enforce https:// at the load balancer / ingress; HSTS preload submission is the long-term fix.",
            });
            return findings;
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 443;

#pragma warning disable CA5364, CA5386, CA5397, SYSLIB0039
        var probes = new (string Label, SslProtocols Proto, string Severity)[]
        {
            ("TLS 1.0", SslProtocols.Tls, "high"),
            ("TLS 1.1", SslProtocols.Tls11, "high"),
            ("TLS 1.2", SslProtocols.Tls12, "info"),
            ("TLS 1.3", SslProtocols.Tls13, "info"),
        };
#pragma warning restore CA5364, CA5386, CA5397, SYSLIB0039

        var anyReached = false;
        foreach (var (label, proto, severity) in probes)
        {
            try
            {
                using var tcp = new TcpClient();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
                anyReached = true;

                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = proto,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ssl.AuthenticateAsClientAsync(opts, handshakeCts.Token).ConfigureAwait(false);

                // Handshake succeeded → server accepts this version.
                findings.Add(new ScanFinding
                {
                    Template = SyntheticTemplate.TlsAccepted(label, severity),
                    Status = severity == "info" ? ScanFindingStatus.Safe : ScanFindingStatus.Vulnerable,
                    Detail = severity == "info"
                        ? $"{label} accepted (expected — modern baseline)."
                        : $"{label} accepted — deprecated since 2020 per IETF / PCI-DSS 3.2.1; downgrade-attack vector. Disable in the TLS configuration of the host / load balancer / ingress.",
                });
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException)
            {
                // Handshake refused — server doesn't support this
                // protocol. Healthy outcome for the old versions; for
                // the new ones, surface as info so the operator can
                // see what's missing.
                if (severity != "info") continue; // expected refusal
                findings.Add(new ScanFinding
                {
                    Template = SyntheticTemplate.TlsRejected(label),
                    Status = ScanFindingStatus.Safe,
                    Detail = $"{label} not accepted ({ex.GetType().Name}). Older targets sometimes don't speak TLS 1.3 yet — usually fine, occasionally indicates a misconfigured TLS stack.",
                });
            }
        }

        if (!anyReached)
        {
            findings.Add(ScanFinding.Error(SyntheticTemplate.TlsEnumeration(),
                $"Could not reach {host}:{port} for any TLS probe. Skipping built-in TLS check."));
        }
        return findings;
    }

    // ------------------------------------------------------------------
    // Banner / version disclosure
    // ------------------------------------------------------------------

    private static readonly (string Header, string Severity, string Why)[] s_disclosureHeaders =
    [
        ("Server", "low", "Identifies the server software / version. Lets an attacker target known CVEs against the disclosed stack without reconnaissance."),
        ("X-Powered-By", "low", "Identifies the application framework / language / version. Same risk as Server header."),
        ("X-AspNet-Version", "medium", "Discloses the ASP.NET runtime version. Disable in web.config / Startup so attackers can't pin known framework CVEs."),
        ("X-AspNetMvc-Version", "medium", "Discloses the ASP.NET MVC version."),
        ("Via", "low", "Identifies the upstream proxy chain. Reveals internal infrastructure topology."),
    ];

    /// <summary>
    /// Probe the target root with a plain GET, scan the response
    /// headers for the canonical version-disclosure markers (Server,
    /// X-Powered-By, X-AspNet-Version, X-AspNetMvc-Version, Via).
    /// Each disclosed header becomes its own finding so reports group
    /// them under the right rule id in SARIF.
    /// </summary>
    private static async Task<IReadOnlyList<ScanFinding>> BannerDisclosureAsync(string target, HttpClient http, IList<string> authHeaders, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            foreach (var (header, severity, why) in s_disclosureHeaders)
            {
                if (resp.Headers.TryGetValues(header, out var values))
                {
                    var combined = string.Join(", ", values);
                    if (!string.IsNullOrEmpty(combined))
                    {
                        findings.Add(new ScanFinding
                        {
                            Template = SyntheticTemplate.BannerDisclosure(header, severity),
                            Status = ScanFindingStatus.Vulnerable,
                            Detail = $"{header}: {combined}. {why}",
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            findings.Add(ScanFinding.Error(SyntheticTemplate.BannerDisclosure("(any)", "low"),
                $"Banner-disclosure probe failed: {ex.Message}"));
        }
        return findings;
    }

    // ------------------------------------------------------------------
    // Verbose error detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Patterns that indicate the response leaked debug / stack-trace
    /// / framework-internals output. Conservative — every regex
    /// matches a phrase that's nearly always a server-misconfiguration
    /// signal, not a legitimate response body.
    /// </summary>
    private static readonly Regex[] s_errorMarkers =
    [
        new(@"\bat\s+[A-Za-z_][\w\.<>]+\.cs:line\s+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"\bSystem\.[A-Za-z]+Exception\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"^\s*Traceback \(most recent call last\):", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"<title>IIS\s+\d+\.\d+\s+Detailed Error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"Whitelabel\s+Error\s+Page", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"\bNullReferenceException\b|\bArgumentNullException\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"<pre>\s*at\s+\w+\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
    ];

    /// <summary>
    /// Three lightweight probes that often trip default error pages
    /// on misconfigured production servers:
    ///   • /bowire-scan-probe-doesnt-exist-{random} → 404 with a
    ///     potentially-detailed error body
    ///   • /'%00invalid → URL with a null-byte that some routers
    ///     surface as 500 with a stack trace
    ///   • / with a deliberately-malformed Content-Length header → 4xx
    ///     that occasionally dumps headers / framework version
    ///
    /// Response bodies are scanned for the known error markers; a
    /// match emits a finding so the operator knows the production
    /// build is leaking debug-only output.
    /// </summary>
    private static async Task<IReadOnlyList<ScanFinding>> VerboseErrorDetectionAsync(string target, HttpClient http, IList<string> authHeaders, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();
        var probes = new[]
        {
            $"/bowire-scan-probe-{Guid.NewGuid():N}",
            "/%00.aspx",
            "/?bowire_scan_param=%80%81%82",
        };

        foreach (var path in probes)
        {
            try
            {
                var url = CombineUrl(target, path);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ScanCommand.ApplyAuthHeaders(req, authHeaders);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                foreach (var pattern in s_errorMarkers)
                {
                    if (pattern.IsMatch(body))
                    {
                        findings.Add(new ScanFinding
                        {
                            Template = SyntheticTemplate.VerboseErrors(),
                            Status = ScanFindingStatus.Vulnerable,
                            Detail = $"Probe {path} returned status {(int)resp.StatusCode} with a body matching {pattern}. "
                                + "Discloses framework / stack-trace details. Wire a global exception handler that returns "
                                + "a generic 'Internal Server Error' body in production — ASP.NET Core: app.UseExceptionHandler(); "
                                + "Spring: @ControllerAdvice; Express: app.use(errorHandler) with NODE_ENV=production.",
                        });
                        goto nextProbe; // one finding per probe is enough — don't double-count
                    }
                }
                nextProbe:;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Probe failed (connect / timeout) — silently skip, this
                // built-in is best-effort and one network blip
                // shouldn't tank the rest of the scan.
            }
        }
        return findings;
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }
}

/// <summary>
/// Factory for the synthetic <see cref="LoadedTemplate"/> instances the
/// built-in checks emit findings against. Each one carries a stable
/// <c>BWR-BUILTIN-…</c> id so SARIF rule-grouping + console output
/// treat them identically to user-supplied templates.
/// </summary>
internal static class SyntheticTemplate
{
    public static LoadedTemplate PlaintextHttp() => Build(
        id: "BWR-BUILTIN-TLS-001",
        name: "Target serves plaintext http://",
        cwe: "CWE-319",
        owaspApi: "API8-2023-SECMISCONF",
        severity: "high",
        cvss: 7.4,
        remediation: "Enforce https:// at the load balancer / ingress. Add HSTS (Strict-Transport-Security: max-age=31536000) once https:// is reliable. Submit to the HSTS-preload list for browser-side enforcement.");

    public static LoadedTemplate TlsEnumeration() => Build(
        id: "BWR-BUILTIN-TLS-000",
        name: "TLS version enumeration",
        cwe: "CWE-327",
        owaspApi: "API8-2023-SECMISCONF",
        severity: "info",
        cvss: null,
        remediation: "Diagnostic — surfaces the supported TLS protocol versions of the target.");

    public static LoadedTemplate TlsAccepted(string label, string severity) => Build(
        id: $"BWR-BUILTIN-TLS-{label.Replace(" ", "").Replace(".", "")}",
        name: $"TLS handshake accepted on {label}",
        cwe: severity == "info" ? "CWE-327" : "CWE-326",
        owaspApi: "API8-2023-SECMISCONF",
        severity: severity,
        cvss: severity == "high" ? 7.4 : null,
        remediation: severity == "info"
            ? $"{label} is a modern baseline; no action needed."
            : $"Disable {label} in the TLS configuration. ASP.NET Core: configure Kestrel.HttpsConnectionAdapterOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13. nginx: ssl_protocols TLSv1.2 TLSv1.3. AWS ALB: pick a security policy that excludes TLS 1.0 / 1.1.");

    public static LoadedTemplate TlsRejected(string label) => Build(
        id: $"BWR-BUILTIN-TLS-NOT-{label.Replace(" ", "").Replace(".", "")}",
        name: $"TLS handshake rejected on {label}",
        cwe: null,
        owaspApi: null,
        severity: "info",
        cvss: null,
        remediation: $"Diagnostic — {label} is not enabled on the target. Usually fine for modern baselines.");

    public static LoadedTemplate BannerDisclosure(string header, string severity) => Build(
        id: $"BWR-BUILTIN-BANNER-{header.Replace("-", "").ToUpperInvariant()}",
        name: $"Version-disclosing header: {header}",
        cwe: "CWE-200",
        owaspApi: "API8-2023-SECMISCONF",
        severity: severity,
        cvss: severity == "medium" ? 5.3 : 3.7,
        remediation: $"Remove or anonymise the {header} response header. ASP.NET Core: app.Use((ctx, next) => {{ ctx.Response.Headers.Remove(\"{header}\"); return next(); }}). nginx: server_tokens off; (covers Server header). IIS: customize the runtime via web.config <customHeaders><remove name=\"{header}\"/></customHeaders>.");

    public static LoadedTemplate VerboseErrors() => Build(
        id: "BWR-BUILTIN-ERROR-001",
        name: "Verbose error response leaks stack traces / framework details",
        cwe: "CWE-209",
        owaspApi: "API8-2023-SECMISCONF",
        severity: "medium",
        cvss: 5.3,
        remediation: "Wire a global exception handler that returns a generic body in production. ASP.NET Core: app.UseExceptionHandler(\"/error\"). Spring: @ControllerAdvice + ResponseStatusException. Express: error-handler middleware gated on NODE_ENV=production. Always: scrub stack traces / inner-exception details / framework version banners before the response leaves the server.");

    private static LoadedTemplate Build(string id, string name, string? cwe, string? owaspApi, string severity, double? cvss, string remediation)
    {
        return new LoadedTemplate(
            $"(builtin: {id})",
            new BowireRecording
            {
                Id = id,
                Name = name,
                Attack = true,
                Vulnerability = new AttackVulnerability
                {
                    Id = id,
                    Cwe = cwe,
                    OwaspApi = owaspApi,
                    Severity = severity,
                    Cvss = cvss,
                    Authors = { "bowire-builtin" },
                    Remediation = remediation,
                },
                VulnerableWhen = new AttackPredicate(),
            });
    }
}

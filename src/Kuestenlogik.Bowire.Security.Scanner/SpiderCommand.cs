// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Implementation of <c>bowire scan spider</c> (#176) — discover candidate
/// endpoints from a base URL so the scanner / OWASP suite has an attack
/// surface to work against, including endpoints the schema forgot to declare.
/// </summary>
/// <remarks>
/// Conservative by design: same-host (or <c>--scope</c>) only, honours
/// <c>robots.txt</c> Disallow unless <c>--no-robots</c>, never authenticates
/// beyond the operator's <c>--auth-header</c>, and caps the candidate count.
/// Discovery sources: <c>robots.txt</c>, <c>sitemap.xml</c>, an OpenAPI/Swagger
/// document's <c>paths</c>, a curated common-path HEAD sweep, and same-origin
/// links on the base page. Candidates are surfaced for the operator to confirm
/// or ignore — never auto-added to a schema.
/// </remarks>
public static partial class SpiderCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = true };

    // Curated API-ish paths worth a HEAD probe. Modest on purpose.
    private static readonly string[] s_commonPaths =
    [
        "/api", "/api/v1", "/api/v2", "/api/v3", "/v1", "/v2", "/graphql",
        "/openapi.json", "/swagger.json", "/swagger", "/swagger/v1/swagger.json",
        "/api-docs", "/v3/api-docs", "/health", "/healthz", "/status", "/metrics",
        "/actuator", "/.well-known/security.txt", "/robots.txt", "/sitemap.xml",
        "/docs", "/redoc", "/users", "/login", "/admin",
    ];

    private static readonly string[] s_openApiProbePaths =
    [
        "/openapi.json", "/swagger.json", "/swagger/v1/swagger.json", "/v3/api-docs", "/api-docs",
    ];

    [GeneratedRegex("""(?:href|src|action)\s*=\s*["']([^"'#\s]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLink();

    [GeneratedRegex("<loc>\\s*([^<\\s]+)\\s*</loc>", RegexOptions.IgnoreCase)]
    private static partial Regex SitemapLoc();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CheckCertificateRevocationList is set explicitly in the else branch when the operator hasn't opted into self-signed certs — same posture as ScanCommand.")]
    public static async Task<int> RunAsync(SpiderOptions options, CancellationToken ct, TextWriter? output = null, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (string.IsNullOrWhiteSpace(options.Url) || !Uri.TryCreate(options.Url, UriKind.Absolute, out var baseUri))
        {
            await stderr.WriteLineAsync("  Usage: bowire scan spider --url <base-url> [--scope <host>] [--no-robots] [--out <json>]").ConfigureAwait(false);
            return 2;
        }
        var authority = baseUri.GetLeftPart(UriPartial.Authority);
        var inScope = ScanCommand.CompileScope(options.Scope, options.Url);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (options.AllowSelfSignedCerts) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        else handler.CheckCertificateRevocationList = true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 30) };

        var candidates = new Dictionary<string, SpiderCandidate>(StringComparer.OrdinalIgnoreCase);
        void Add(string method, string url, string source, int status)
        {
            if (candidates.Count >= options.MaxCandidates) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
            if (!inScope(u.Host)) return;
            var key = method + " " + u.GetLeftPart(UriPartial.Path);
            if (!candidates.ContainsKey(key))
                candidates[key] = new SpiderCandidate(method, u.GetLeftPart(UriPartial.Path), source, status);
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  Spidering {authority}  (scope: {(options.Scope is { Count: > 0 } ? string.Join(",", options.Scope) : baseUri.Host)}, robots: {(options.RespectRobots ? "respected" : "ignored")})").ConfigureAwait(false);

        // 1. robots.txt — collect Disallow globs + Sitemap references.
        var disallow = new List<string>();
        var (robotsStatus, robotsBody) = await GetAsync(http, authority + "/robots.txt", options.AuthHeaders, ct).ConfigureAwait(false);
        if (robotsStatus is >= 200 and < 300 && !string.IsNullOrEmpty(robotsBody))
        {
            foreach (var line in robotsBody.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
                {
                    var path = trimmed["Disallow:".Length..].Trim();
                    if (path.Length > 0) { disallow.Add(path); Add("GET", authority + path.TrimEnd('*'), "robots.txt", -1); }
                }
                else if (trimmed.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                {
                    await AddSitemapAsync(http, trimmed["Sitemap:".Length..].Trim(), options.AuthHeaders, Add, ct).ConfigureAwait(false);
                }
            }
        }

        bool Blocked(string path) => options.RespectRobots && disallow.Any(d =>
            path.StartsWith(d.TrimEnd('*'), StringComparison.OrdinalIgnoreCase) && d.TrimEnd('*').Length > 0);

        // 2. sitemap.xml at the conventional location.
        await AddSitemapAsync(http, authority + "/sitemap.xml", options.AuthHeaders, Add, ct).ConfigureAwait(false);

        // 3. OpenAPI / Swagger — enumerate every declared path + method.
        // Probe both the authority root and the base URL's own path prefix
        // (so `--url .../api/v3` finds `.../api/v3/openapi.json`).
        var basePrefix = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var openApiUrls = new List<string>();
        foreach (var probe in s_openApiProbePaths)
        {
            openApiUrls.Add(authority + probe);
            if (!string.Equals(basePrefix, authority, StringComparison.OrdinalIgnoreCase))
                openApiUrls.Add(basePrefix + probe);
        }
        foreach (var oaUrl in openApiUrls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (st, body) = await GetAsync(http, oaUrl, options.AuthHeaders, ct).ConfigureAwait(false);
            if (st is >= 200 and < 300 && LooksJson(body) && TryAddOpenApiPaths(body, authority, Add))
                break; // first valid OpenAPI doc wins
        }

        // 3b. GraphQL introspection — one candidate per query / mutation operation.
        const string introspection = "{\"query\":\"query{__schema{queryType{name fields{name}} mutationType{name fields{name}}}}\"}";
        var gqlEndpoints = new List<string> { authority + "/graphql", authority + "/api/graphql", authority + "/query", options.Url };
        if (!string.Equals(basePrefix, authority, StringComparison.OrdinalIgnoreCase)) gqlEndpoints.Add(basePrefix + "/graphql");
        foreach (var gql in gqlEndpoints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(gql, UriKind.Absolute, out var gu) || !inScope(gu.Host)) continue;
            var (st, body) = await PostJsonAsync(http, gql, introspection, options.AuthHeaders, ct).ConfigureAwait(false);
            if (st is < 200 or >= 300 || !body.Contains("__schema", StringComparison.Ordinal)) continue;
            var ops = ParseGraphQLOps(body, gu.GetLeftPart(UriPartial.Path));
            if (ops.Count == 0) continue;
            foreach (var op in ops)
            {
                if (candidates.Count >= options.MaxCandidates) break;
                candidates.TryAdd($"gql {op.Url} {op.Source}", op);
            }
            break; // first introspectable endpoint wins
        }

        // 4. Common-path HEAD sweep — reachable (non-404) = candidate.
        foreach (var path in s_commonPaths)
        {
            if (Blocked(path)) continue;
            var st = await HeadAsync(http, authority + path, options.AuthHeaders, ct).ConfigureAwait(false);
            if (st > 0 && st != 404 && st != 410) Add("GET", authority + path, "common-path", st);
        }

        // 5. Same-origin links on the base page (shallow — depth 1).
        var (baseStatus, baseBody) = await GetAsync(http, options.Url, options.AuthHeaders, ct).ConfigureAwait(false);
        if (baseStatus is >= 200 and < 300 && !string.IsNullOrEmpty(baseBody) && baseBody.Length < 2_000_000)
        {
            foreach (Match m in HtmlLink().Matches(baseBody))
            {
                var href = m.Groups[1].Value;
                var abs = ResolveUrl(baseUri, href);
                if (abs is not null && !Blocked(new Uri(abs).AbsolutePath)) Add("GET", abs, "page-link", -1);
            }
        }

        var ordered = candidates.Values.OrderBy(c => c.Url, StringComparer.Ordinal).ToList();
        await WriteReportAsync(ordered, stdout).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(options.OutJson))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutJson))!);
            await File.WriteAllTextAsync(options.OutJson, JsonSerializer.Serialize(ordered, s_jsonOpts), ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Candidates → {options.OutJson}").ConfigureAwait(false);
        }
        return 0;
    }

    // ---- sources ----

    private static async Task AddSitemapAsync(HttpClient http, string sitemapUrl, IList<string> auth, Action<string, string, string, int> add, CancellationToken ct)
    {
        var (st, body) = await GetAsync(http, sitemapUrl, auth, ct).ConfigureAwait(false);
        if (st is < 200 or >= 300 || string.IsNullOrEmpty(body)) return;
        foreach (Match m in SitemapLoc().Matches(body))
            add("GET", m.Groups[1].Value.Trim(), "sitemap.xml", -1);
    }

    private static bool TryAddOpenApiPaths(string body, string authority, Action<string, string, string, int> add)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                return false;
            var basePath = ReadOpenApiBasePath(doc.RootElement);
            var any = false;
            foreach (var path in paths.EnumerateObject())
            {
                var url = authority + CombinePath(basePath, path.Name);
                if (path.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var method in path.Value.EnumerateObject())
                    {
                        if (IsHttpMethod(method.Name)) { add(method.Name.ToUpperInvariant(), url, "openapi", -1); any = true; }
                    }
                }
                if (!any) { add("GET", url, "openapi", -1); any = true; }
            }
            return any;
        }
        catch (JsonException) { return false; }
    }

    private static string ReadOpenApiBasePath(JsonElement root)
    {
        // OpenAPI 3: servers[0].url path; Swagger 2: basePath.
        if (root.TryGetProperty("basePath", out var bp) && bp.ValueKind == JsonValueKind.String)
            return bp.GetString() ?? "";
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array
            && servers.GetArrayLength() > 0 && servers[0].TryGetProperty("url", out var su) && su.ValueKind == JsonValueKind.String)
        {
            var s = su.GetString() ?? "";
            return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimEnd('/') : s.TrimEnd('/');
        }
        return "";
    }

    private static bool IsHttpMethod(string m) => m.ToUpperInvariant() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";

    private static string CombinePath(string basePath, string path)
    {
        if (string.IsNullOrEmpty(basePath) || basePath == "/") return path.StartsWith('/') ? path : "/" + path;
        return basePath.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
    }

    private static string? ResolveUrl(Uri baseUri, string href)
    {
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.TryCreate(baseUri, href, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https") ? abs.ToString() : null;
    }

    private static bool LooksJson(string body) => !string.IsNullOrWhiteSpace(body) && body.TrimStart() is ['{', ..];

    // ---- http ----

    private static async Task<(int Status, string Body)> GetAsync(HttpClient http, string url, IList<string> auth, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ScanCommand.ApplyAuthHeaders(req, auth);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException) { return (-1, ""); }
    }

    private static async Task<(int Status, string Body)> PostJsonAsync(HttpClient http, string url, string json, IList<string> auth, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            ScanCommand.ApplyAuthHeaders(req, auth);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException) { return (-1, ""); }
    }

    // Parse a GraphQL introspection response into one candidate per query /
    // mutation operation (the GraphQL analogue of an endpoint).
    private static List<SpiderCandidate> ParseGraphQLOps(string body, string endpointPath)
    {
        var ops = new List<SpiderCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("__schema", out var schema))
            {
                CollectGraphQLOps(schema, "queryType", "query", endpointPath, ops);
                CollectGraphQLOps(schema, "mutationType", "mutation", endpointPath, ops);
            }
        }
        catch (JsonException) { /* not a schema — ignore */ }
        return ops;
    }

    private static void CollectGraphQLOps(JsonElement schema, string typeProp, string kind, string endpointPath, List<SpiderCandidate> ops)
    {
        if (!schema.TryGetProperty(typeProp, out var t) || t.ValueKind != JsonValueKind.Object) return;
        if (!t.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) return;
        foreach (var f in fields.EnumerateArray())
        {
            if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                ops.Add(new SpiderCandidate("POST", endpointPath, $"graphql:{kind} {n.GetString()}", 200));
        }
    }

    private static async Task<int> HeadAsync(HttpClient http, string url, IList<string> auth, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            ScanCommand.ApplyAuthHeaders(req, auth);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)resp.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException) { return -1; }
    }

    private static async Task WriteReportAsync(List<SpiderCandidate> candidates, TextWriter stdout)
    {
        await stdout.WriteLineAsync().ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            await stdout.WriteLineAsync("  No candidate endpoints discovered.").ConfigureAwait(false);
            return;
        }
        await stdout.WriteLineAsync($"  {candidates.Count} candidate endpoint(s) — confirm the real ones into your workspace; treat the rest as potential leaks:").ConfigureAwait(false);
        foreach (var c in candidates)
        {
            var st = c.Status > 0 ? c.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            await stdout.WriteLineAsync($"    {c.Method,-7} {st,-4} {c.Url}   ({c.Source})").ConfigureAwait(false);
        }
    }
}

/// <summary>One discovered endpoint candidate.</summary>
public sealed record SpiderCandidate(string Method, string Url, string Source, int Status);

/// <summary>Options for <c>bowire scan spider</c>.</summary>
public sealed class SpiderOptions
{
    public string Url { get; init; } = "";
    public IList<string> Scope { get; init; } = new List<string>();
    public IList<string> AuthHeaders { get; init; } = new List<string>();
    public int TimeoutSeconds { get; init; } = 30;
    public bool RespectRobots { get; init; } = true;
    public bool AllowSelfSignedCerts { get; init; }
    public int MaxCandidates { get; init; } = 500;
    public string? OutJson { get; init; }
}

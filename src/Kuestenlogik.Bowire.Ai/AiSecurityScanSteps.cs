// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Security;
using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Concrete <see cref="ISecurityScanSteps"/> for #104 — wires the orchestration
/// engine to the real work: an <see cref="IChatClient"/> drives the threat-model
/// + triage + report-summary stages, and an injected
/// <see cref="ISecurityScanProbeRunner"/> executes the probe stage. Every AI
/// stage degrades to a deterministic default when no model is connected or the
/// model's output can't be parsed, so the pipeline always completes:
/// threat-model → a path heuristic; triage → keep-by-default; report → the
/// deterministic markdown without the AI summary.
/// </summary>
public sealed partial class AiSecurityScanSteps : ISecurityScanSteps
{
    private readonly IChatClient? _client;
    private readonly ISecurityScanProbeRunner? _probeRunner;
    private readonly string _target;

    public AiSecurityScanSteps(IChatClient? client, ISecurityScanProbeRunner? probeRunner, string target)
    {
        _client = client;
        _probeRunner = probeRunner;
        _target = target ?? "";
    }

    public async Task<IReadOnlyList<RankedEndpoint>> ThreatModelAsync(IReadOnlyList<OrchestratorEndpoint> endpoints, CancellationToken ct)
    {
        if (_client is not null)
        {
            try
            {
                var system = """
                    You rank API endpoints by attack-surface risk for a security scan. Given a JSON list
                    of endpoints, respond ONLY with a JSON array: [{"endpointId":"...","risk":<0-10 int>,"reason":"<short>"}].
                    Higher risk = more likely to carry an authz / injection / data-exposure bug (admin, auth,
                    user-data, id-in-path, write methods). No prose outside the JSON.
                    """;
                var user = JsonSerializer.Serialize(endpoints.Select(e => new { e.EndpointId, e.Path, e.Method }));
                var response = await _client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)], cancellationToken: ct).ConfigureAwait(false);
                var ranked = ParseRanking(response.Text, endpoints);
                if (ranked.Count > 0) return ranked;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // fall through to the heuristic
            }
        }
        return endpoints.Select(e => new RankedEndpoint(e, HeuristicRisk(e), "path heuristic")).ToArray();
    }

    public Task<IReadOnlyList<OrchestratedFinding>> ProbeAsync(RankedEndpoint endpoint, CancellationToken ct)
        => _probeRunner is null
            ? Task.FromResult<IReadOnlyList<OrchestratedFinding>>([])
            : _probeRunner.RunAsync(endpoint.Endpoint, _target, ct);

    public async Task<(int RealScore, string? Reasoning)> TriageAsync(OrchestratedFinding finding, CancellationToken ct)
    {
        if (_client is null) return (100, "kept by default (no AI triage available)");
        try
        {
            var system = """
                You triage a security finding real-vs-false-positive. Respond ONLY with a JSON object:
                {"realScore":<0-100 int>,"reasoning":"<one sentence>"}. Be conservative; when evidence is thin, score below 50.
                """;
            var user = $"Endpoint: {finding.EndpointId}\nRule: {finding.RuleId}\nTitle: {finding.Title}\nSeverity: {finding.Severity}\nOWASP: {finding.OwaspApi}";
            var response = await _client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)], cancellationToken: ct).ConfigureAwait(false);
            return ParseTriage(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (100, "kept by default (AI triage failed)");
        }
    }

    public async Task<string?> ReportAsync(OrchestratedScanResult interim, CancellationToken ct)
    {
        var deterministic = BuildMarkdown(interim, summary: null);
        if (_client is null || interim.Findings.Count == 0) return deterministic;
        try
        {
            var system = """
                Write a 3-5 sentence executive summary of an API security scan from the finding list.
                Plain prose, no markdown headings or bullets, no invented findings.
                """;
            var response = await _client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, deterministic)], cancellationToken: ct).ConfigureAwait(false);
            return BuildMarkdown(interim, response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return deterministic;
        }
    }

    // ---- deterministic helpers ----

    private static string BuildMarkdown(OrchestratedScanResult r, string? summary)
    {
        var sb = new StringBuilder();
        sb.Append("# AI security scan\n\n");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"Ranked {r.Ranked.Count} endpoint(s); probed {r.Probed.Count}; kept {r.Findings.Count} finding(s), suppressed {r.SuppressedCount} likely false-positive(s).\n\n");
        if (!string.IsNullOrWhiteSpace(summary))
            sb.Append("## Executive summary\n\n").Append(summary.Trim()).Append("\n\n");
        sb.Append("## Findings\n\n");
        if (r.Findings.Count == 0) sb.Append("No confirmed findings.\n");
        else
            foreach (var f in r.Findings.OrderByDescending(f => f.RealScore))
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"- **[{f.Severity}] {f.Title}** ({f.EndpointId}, `{f.RuleId}`, real {f.RealScore}%){(f.OwaspApi is null ? "" : " — " + f.OwaspApi)}\n");
        return sb.ToString();
    }

    private static int HeuristicRisk(OrchestratorEndpoint e)
    {
        var p = e.Path ?? "";
        var score = 3;
        if (AdminRx().IsMatch(p)) score += 4;
        if (AuthRx().IsMatch(p)) score += 3;
        if (UserRx().IsMatch(p)) score += 2;
        if (IdRx().IsMatch(p)) score += 2;
        if (e.Method is "POST" or "PUT" or "PATCH" or "DELETE") score += 1;
        return Math.Clamp(score, 0, 10);
    }

    private static List<RankedEndpoint> ParseRanking(string? text, IReadOnlyList<OrchestratorEndpoint> endpoints)
    {
        var byId = endpoints.ToDictionary(e => e.EndpointId, StringComparer.Ordinal);
        var result = new List<RankedEndpoint>();
        try
        {
            using var doc = JsonDocument.Parse(StripFences(text));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.TryGetProperty("endpointId", out var idEl) ? idEl.GetString() : null;
                if (id is null || !byId.TryGetValue(id, out var ep)) continue;
                var risk = el.TryGetProperty("risk", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                var reason = el.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null;
                result.Add(new RankedEndpoint(ep, Math.Clamp(risk, 0, 10), reason));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    private static (int, string?) ParseTriage(string? text)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFences(text));
            var score = doc.RootElement.TryGetProperty("realScore", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 50;
            var reason = doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
            return (Math.Clamp(score, 0, 100), reason);
        }
        catch (JsonException)
        {
            return (50, "unparseable triage response — kept conservatively");
        }
    }

    // Strip a leading ```json / ``` fence and trailing ``` the model may add.
    private static string StripFences(string? text)
    {
        var t = (text ?? "").Trim();
        var m = FenceRx().Match(t);
        return m.Success ? m.Groups[1].Value.Trim() : t;
    }

    [GeneratedRegex(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline)]
    private static partial Regex FenceRx();
    [GeneratedRegex(@"/(admin|internal|debug|sudo|root|management)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdminRx();
    [GeneratedRegex(@"/(auth|login|token|oauth|sso|saml)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AuthRx();
    [GeneratedRegex(@"/(users?|accounts?|profile|members?|customers?|tenants?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UserRx();
    [GeneratedRegex(@"\{[^}]+\}|/:\w+|/\d+(/|$)")]
    private static partial Regex IdRx();
}

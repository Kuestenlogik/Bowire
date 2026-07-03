// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Provider-side contract verification (#191). Replays every interaction
/// in a consumer contract against the provider's live base URL and
/// checks the actual response satisfies the contract: the status code
/// must match, and the response body must satisfy the expected shape
/// (structural match — the provider may add fields / vary values, but
/// every field the consumer relies on must be present with the same JSON
/// kind). Results project into a <see cref="RunReport"/> so the JUnit /
/// SARIF / HTML emitters and exit-code contract are shared verbatim with
/// <c>bowire test</c>.
/// </summary>
internal static class ContractVerifier
{
    /// <summary>
    /// Verify <paramref name="contract"/> against <paramref name="baseUrl"/>.
    /// Returns a report; <see cref="RunReport.FailedTests"/> &gt; 0 means
    /// the provider broke the contract.
    /// </summary>
    public static async Task<RunReport> VerifyAsync(
        HttpClient http, PactContract contract, string baseUrl, TextWriter stdout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(contract);

        var report = new RunReport
        {
            CollectionName = $"{contract.Consumer.Name} → {contract.Provider.Name}",
            CollectionPath = string.Empty,
            StartedAt = DateTime.UtcNow,
        };
        var sw = Stopwatch.StartNew();
        var baseTrim = baseUrl.TrimEnd('/');

        foreach (var interaction in contract.Interactions)
        {
            var result = await VerifyInteractionAsync(http, interaction, baseTrim, ct).ConfigureAwait(false);
            report.Tests.Add(result);
            report.TotalAssertions += result.Assertions.Count;
            var anyFailed = false;
            foreach (var a in result.Assertions)
            {
                if (a.Passed) report.PassedAssertions++;
                else anyFailed = true;
            }
            if (anyFailed || !string.IsNullOrEmpty(result.Error)) report.FailedTests++;

            await PrintAsync(stdout, result).ConfigureAwait(false);
        }

        sw.Stop();
        report.DurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    private static async Task<TestResult> VerifyInteractionAsync(
        HttpClient http, PactInteraction interaction, string baseUrl, CancellationToken ct)
    {
        var result = new TestResult
        {
            Name = interaction.Description,
            Method = interaction.Request.Method,
        };

        HttpResponseMessage? resp = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = BuildRequest(interaction.Request, baseUrl);
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Error = $"request failed: {ex.Message}";
            resp?.Dispose();
            return result;
        }

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        var actualStatus = (int)resp.StatusCode;
        result.Status = actualStatus.ToString(CultureInfo.InvariantCulture);
        string actualBody;
        try { actualBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            actualBody = string.Empty;
            result.Error = $"could not read response body: {ex.Message}";
        }
        result.Response = actualBody;
        resp.Dispose();

        // Status assertion.
        result.Assertions.Add(new AssertionResult
        {
            Path = "status",
            Op = "eq",
            Expected = interaction.Response.Status.ToString(CultureInfo.InvariantCulture),
            ActualText = actualStatus.ToString(CultureInfo.InvariantCulture),
            Passed = actualStatus == interaction.Response.Status,
        });

        // Body assertion — structural match against the contract's
        // expected body. A null expected body means "any body is fine".
        if (interaction.Response.Body is JsonNode expected)
        {
            var diffs = FlowSnapshotComparer.Compare(
                expected.ToJsonString(), actualBody, FlowSnapshotMode.Structural);
            result.Assertions.Add(new AssertionResult
            {
                Path = "body",
                Op = "matches-shape",
                Expected = Truncate(expected.ToJsonString()),
                ActualText = Truncate(actualBody),
                Passed = diffs.Count == 0,
                Error = diffs.Count == 0 ? null : string.Join("; ", diffs),
            });
        }

        return result;
    }

    private static HttpRequestMessage BuildRequest(PactRequest request, string baseUrl)
    {
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
        var req = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(baseUrl + path));

        if (request.Body is JsonNode body)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        if (request.Headers is not null)
        {
            foreach (var (k, v) in request.Headers)
            {
                // Content-* headers belong on the content, not the
                // request; TryAddWithoutValidation routes them and skips
                // the ones HttpClient manages itself (e.g. Content-Length).
                if (k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    req.Content?.Headers.TryAddWithoutValidation(k, v);
                }
                else if (!req.Headers.TryAddWithoutValidation(k, v))
                {
                    // Host / restricted headers — ignore silently.
                }
            }
        }
        return req;
    }

    private static async Task PrintAsync(TextWriter stdout, TestResult result)
    {
        var ok = string.IsNullOrEmpty(result.Error) && result.Assertions.TrueForAll(a => a.Passed);
        await stdout.WriteLineAsync($"  {(ok ? "PASS" : "FAIL")}  {result.Method}  {result.Name}   {result.Status} · {result.DurationMs}ms").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.Error))
        {
            await stdout.WriteLineAsync($"        error: {result.Error}").ConfigureAwait(false);
            return;
        }
        foreach (var a in result.Assertions)
        {
            if (a.Passed) continue;
            var detail = a.Error ?? $"expected {a.Expected}, got {a.ActualText}";
            await stdout.WriteLineAsync($"        ✗ {a.Path} {a.Op} — {detail}").ConfigureAwait(false);
        }
    }

    private static string Truncate(string s)
        => s.Length <= 80 ? s : string.Concat(s.AsSpan(0, 80), "…");
}

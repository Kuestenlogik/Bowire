// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for Server-Sent Events, rolling up to <c>API2:2023 — Broken
/// Authentication</c>. When <c>--auth-header</c> asserts a credential is
/// expected, the probe subscribes to the SSE endpoint <em>anonymously</em> and
/// watches for a stream that opens and emits — an unauthenticated event stream,
/// the SSE analog of the WebSocket / MQTT auth-bypass checks.
///
/// <para>Read-only (a one-way GET server→client stream, no messages sent). It
/// relies on the plugin's content-type guard: a plain <c>200</c> page that
/// isn't <c>text/event-stream</c> comes back as an error envelope, so it is
/// skipped rather than misreported as an open stream. A <c>401</c>/<c>403</c>
/// on subscribe is the healthy case; silent-only unless <c>--auth-header</c> is
/// supplied.</para>
/// </summary>
internal sealed class SseAuthProbe : IOwaspProtocolProbe
{
    /// <summary>How long to wait for the first event before treating the stream as silent / inconclusive.</summary>
    private static readonly TimeSpan FirstEventWindow = TimeSpan.FromSeconds(6);

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API2:2023");

    public string ProtocolId => "sse";

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // Only meaningful when a credential is expected — a public SSE feed
        // accepting anonymous subscribers isn't a vulnerability.
        if (authHeaders.Count == 0)
            return [];

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FirstEventWindow);

            // Empty metadata → no credential on the subscribe request.
            await foreach (var item in protocol.InvokeStreamAsync(target, service: "", method: "", jsonMessages: [],
                showInternalServices: false, metadata: null, cts.Token).ConfigureAwait(false))
            {
                if (IsErrorEnvelope(item))
                {
                    return [Marker(ScanFindingStatus.Skipped, "API2-SSE-NOT-STREAM", "SSE auth check skipped",
                        "The target did not answer with an event-stream (not an SSE endpoint, or it rejected the anonymous subscribe) — auth enforcement not determined.")];
                }

                // A real SSE event arrived on an anonymous subscribe.
                return [Finding("BWR-OWASP-API2-SSE-NOAUTH", "SSE stream open without authentication",
                    "An anonymous SSE subscribe (no credential, despite --auth-header being supplied) opened an event stream and received an event. The real-time feed is readable by any client — authentication enforced on the REST surface is commonly forgotten on the event stream.",
                    "Authenticate the SSE subscribe: validate the credential (token / cookie) on the GET before switching to text/event-stream, and reject unauthenticated subscribers with 401. Validate the Origin header to block cross-site stream reads.")];
            }

            // Stream completed without yielding anything.
            return [Marker(ScanFindingStatus.Skipped, "API2-SSE-INCONCLUSIVE", "SSE auth check inconclusive",
                "The anonymous subscribe returned no events — the target is likely not an SSE endpoint; auth enforcement not determined.")];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our first-event window elapsed: a stream may have opened but sent
            // nothing. Conservative — report inconclusive rather than a guess.
            return [Marker(ScanFindingStatus.Skipped, "API2-SSE-INCONCLUSIVE", "SSE auth check inconclusive",
                $"No event arrived within {FirstEventWindow.TotalSeconds:0}s of an anonymous subscribe — stream silent or not SSE; auth enforcement not determined.")];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            var authRejected = msg.Contains("401", StringComparison.Ordinal) || msg.Contains("403", StringComparison.Ordinal)
                || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
            return authRejected
                ? [Marker(ScanFindingStatus.Safe, "API2-SSE-AUTH-ENFORCED", "SSE subscribe auth enforced",
                    $"An anonymous SSE subscribe was rejected ({msg.Trim()}) — the endpoint enforces authentication on the stream.")]
                : [Marker(ScanFindingStatus.Skipped, "API2-SSE-INCONCLUSIVE", "SSE auth check inconclusive",
                    $"An anonymous SSE subscribe failed ({ex.GetType().Name}) — the target likely isn't an SSE endpoint; auth enforcement not determined.")];
        }
    }

    // A plugin error envelope is a JSON object with a top-level "error" member
    // (the content-type guard emits one for a non-event-stream response).
    private static bool IsErrorEnvelope(string item)
    {
        try
        {
            using var doc = JsonDocument.Parse(item);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-306", owaspApi: Entry.Tag, severity: "high", cvss: 7.5, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the SSE auth probe."),
        Status = status,
        Detail = detail,
    };
}

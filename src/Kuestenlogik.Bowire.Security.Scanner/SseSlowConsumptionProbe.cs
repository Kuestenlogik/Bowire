// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active SSE probe (#398), rolling up to <c>API4:2023 — Unrestricted Resource
/// Consumption</c> (CWE-400). Subscribes to a Server-Sent-Events stream and
/// reads it <i>very slowly</i> for up to <c>--active-duration</c>, watching
/// whether the server drops / backpressures the slow reader or keeps feeding it
/// (unbounded server-side buffering — a slow-consumer memory-exhaustion lever).
///
/// <para>Honest about the window: the stream ending / erroring within the
/// budget ⇒ the server dropped the slow reader (Safe); the server still feeding
/// a deliberately-slow reader at the end of the budget ⇒ "no slow-consumer drop
/// observed within Ns" (the finding names N). True unbounded buffering isn't
/// directly observable from the client, so the verdict is drop-vs-no-drop.</para>
///
/// <para>Slow + aggressive, so <c>--active</c>-gated.</para>
/// </summary>
internal sealed class SseSlowConsumptionProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "sse";

    private const int MaxObserveSeconds = 120;
    // Per-event pause that makes us a deliberately-slow consumer.
    private static readonly TimeSpan s_slowReadDelay = TimeSpan.FromMilliseconds(500);

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(active);
        if (!IsHttp(target)) return [];

        var seconds = Math.Clamp(active.DurationSeconds, 1, MaxObserveSeconds);
        var budget = TimeSpan.FromSeconds(seconds);
        var meta = BuildMetadata(authHeaders);

        // The SSE plugin rebuilds the URL as origin + method-path, so split the
        // target so the reconstructed URL equals the operator's --target exactly.
        var uri = new Uri(target);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        var events = 0;
        var heldToEnd = false;
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(budget);
            var startTicks = Environment.TickCount64;
            try
            {
                await foreach (var _ in protocol.InvokeStreamAsync(
                    origin, service: "", method: path, [], showInternalServices: false, meta, window.Token).ConfigureAwait(false))
                {
                    events++;
                    // Read slowly — a well-behaved server drops / backpressures us.
                    await Task.Delay(s_slowReadDelay, window.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - startTicks >= budget.TotalMilliseconds)
                    {
                        heldToEnd = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Window elapsed while the server was still feeding us → held.
                heldToEnd = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-SSE-SLOWCONSUME-INCONCLUSIVE",
                "SSE slow-consumption probe inconclusive",
                $"Could not read the event stream ({ex.GetType().Name}) — the target may not serve text/event-stream or is unreachable; slow-consumer handling not determined.")];
        }

        if (events == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-SSE-SLOWCONSUME-NO-STREAM",
                "SSE slow-consumption probe skipped — no events",
                "The endpoint delivered no events (not an event-stream, or empty) — nothing to slow-consume.")];
        }

        return heldToEnd
            ? [Finding("BWR-OWASP-API4-SSE-SLOWCONSUME", $"No SSE slow-consumer drop observed within {seconds}s",
                $"The server kept feeding a deliberately-slow reader ({events} event(s), {s_slowReadDelay.TotalMilliseconds:0}ms between reads) for the full {seconds}s budget with no drop / backpressure. A slow consumer can force unbounded server-side buffering (memory exhaustion).",
                $"Bound per-connection send buffers and drop / disconnect slow readers (write timeout, max queued events). Re-run with a longer --active-duration to probe a larger window.",
                "medium", 5.3)]
            : [Marker(ScanFindingStatus.Safe, "API4-SSE-SLOWCONSUME-DROPPED",
                "SSE server drops the slow reader",
                $"The server ended the stream after {events} event(s) while we read slowly — it drops / backpressures slow consumers rather than buffering unboundedly.")];
    }

    private static Dictionary<string, string>? BuildMetadata(IList<string> authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            if (name.Length > 0) meta[name] = raw[(colon + 1)..].TrimStart();
        }
        return meta.Count == 0 ? null : meta;
    }

    private static bool IsHttp(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-400", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active SSE slow-consumption probe."),
        Status = status,
        Detail = detail,
    };
}

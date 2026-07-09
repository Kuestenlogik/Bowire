// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active gRPC probe (#399), rolling up to <c>API4:2023 — Unrestricted Resource
/// Consumption</c>. Discovers a server-streaming method (via reflection) and
/// opens <c>--active-concurrency N</c> concurrent long-lived streams, watching
/// whether the server enforces a per-client / per-connection concurrent-stream
/// (or resource) limit.
///
/// <para>The verdict is honest about N: a stream rejected with
/// <c>RESOURCE_EXHAUSTED</c> before the budget is reached ⇒ the server rate-
/// limits (Safe, reported at the count where it kicked in); all N opening with
/// no rejection ⇒ "no concurrent-stream limit observed at N" (reported as a
/// finding that names N — because HTTP/2 permits 100+ streams by default, a
/// blanket "vulnerable" would be dishonest, so the operator sets N and the
/// finding states it). CWE-770.</para>
///
/// <para>Aggressive (opens many streams), so it runs only under <c>--active</c>;
/// every stream is cancelled as soon as it's classified.</para>
/// </summary>
internal sealed class GrpcConcurrentStreamProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "grpc";

    // Time to wait for a single stream to establish (first frame or a still-open
    // stream) before treating it as opened.
    private static readonly TimeSpan s_openWindow = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(active);
        if (!IsHttp(target)) return [];

        // Find a pure server-streaming method to fan out on (reflection).
        (string Service, string Method)? streaming;
        try
        {
            streaming = await FindServerStreamingMethodAsync(protocol, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRPC-STREAM-INCONCLUSIVE",
                "gRPC concurrent-stream probe inconclusive",
                $"Reflection discovery failed ({ex.GetType().Name}) — the target may not speak gRPC or exposes no reflection; concurrent-stream limit not determined.")];
        }

        if (streaming is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRPC-STREAM-NO-METHOD",
                "gRPC concurrent-stream probe skipped — no server-streaming method",
                "Reflection returned no pure server-streaming method to fan out on; the concurrent-stream limit can't be exercised without one.")];
        }

        var (service, method) = streaming.Value;
        var n = Math.Clamp(active.Concurrency, 1, 1024);
        var meta = BuildMetadata(authHeaders);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, n).Select(_ => TryOpenStreamAsync(protocol, target, service, method, meta, ct)))
            .ConfigureAwait(false);

        var opened = outcomes.Count(o => o == StreamOutcome.Opened);
        var rejected = outcomes.Count(o => o == StreamOutcome.ResourceExhausted);

        if (rejected > 0)
        {
            return [Marker(ScanFindingStatus.Safe, "API4-GRPC-STREAM-LIMITED",
                "gRPC server enforces a concurrent-stream limit",
                $"Of {n} concurrent server-streams attempted, {rejected} were rejected with RESOURCE_EXHAUSTED ({opened} opened) — the server rate-limits concurrent streams / resources.")];
        }

        if (opened > 0)
        {
            return [Finding("BWR-OWASP-API4-GRPC-CONCURRENT-STREAMS", $"No gRPC concurrent-stream limit observed at N={n}",
                $"All {opened} of {n} concurrent server-streams ({service}/{method}) were accepted with no RESOURCE_EXHAUSTED — no per-client concurrent-stream or resource limit was observed at N={n}. A client can open unbounded concurrent streams and exhaust server resources (goroutine/thread/memory pressure).",
                $"Cap concurrent streams per client/connection (gRPC MaxConcurrentStreams / server MaxConcurrentCalls, or a gateway limit) and return RESOURCE_EXHAUSTED past the cap. Re-run with a higher --active-concurrency to probe a larger bound.",
                "medium", 5.3)];
        }

        return [Marker(ScanFindingStatus.Skipped, "API4-GRPC-STREAM-INCONCLUSIVE",
            "gRPC concurrent-stream probe inconclusive",
            $"None of the {n} stream attempts opened (and none were RESOURCE_EXHAUSTED) — the target likely refused the calls for an unrelated reason; limit not determined.")];
    }

    private static async Task<(string Service, string Method)?> FindServerStreamingMethodAsync(
        IBowireProtocol protocol, string target, CancellationToken ct)
    {
        var services = await protocol.DiscoverAsync(target, showInternalServices: false, ct).ConfigureAwait(false);
        foreach (var svc in services)
        {
            foreach (var m in svc.Methods)
            {
                if (m.ServerStreaming && !m.ClientStreaming)
                    return (svc.Name, m.Name);
            }
        }
        return null;
    }

    private static async Task<StreamOutcome> TryOpenStreamAsync(
        IBowireProtocol protocol, string target, string service, string method,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var e = protocol
                .InvokeStreamAsync(target, service, method, ["{}"], showInternalServices: false, metadata, streamCts.Token)
                .GetAsyncEnumerator(streamCts.Token);

            var move = e.MoveNextAsync().AsTask();
            var winner = await Task.WhenAny(move, Task.Delay(s_openWindow, streamCts.Token)).ConfigureAwait(false);
            if (winner == move)
            {
                await move.ConfigureAwait(false); // surface any RpcException
            }
            // Either a frame arrived, the stream ended, or it's still open after
            // the window — in every case the stream was accepted (opened).
            return StreamOutcome.Opened;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LooksResourceExhausted(ex) ? StreamOutcome.ResourceExhausted : StreamOutcome.Errored;
        }
        finally
        {
            await streamCts.CancelAsync().ConfigureAwait(false);
        }
    }

    // gRPC RESOURCE_EXHAUSTED surfaces through the plugin as an exception whose
    // message carries the status name / code (8).
    private static bool LooksResourceExhausted(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message;
            if (m.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || m.Contains("ResourceExhausted", StringComparison.OrdinalIgnoreCase)
                || m.Contains("resource_exhausted", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length > 0) meta[name] = value;
        }
        return meta.Count == 0 ? null : meta;
    }

    private static bool IsHttp(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private enum StreamOutcome { Opened, ResourceExhausted, Errored }

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-770", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active gRPC concurrent-stream probe."),
        Status = status,
        Detail = detail,
    };
}

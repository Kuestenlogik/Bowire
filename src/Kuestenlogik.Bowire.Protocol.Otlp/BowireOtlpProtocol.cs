// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Otlp;

/// <summary>
/// Bowire protocol plugin for the OpenTelemetry Protocol (OTLP) in
/// **passive listener mode** — the workbench opens an OTLP receiver
/// and SUTs export to it via
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:&lt;port&gt;</c>.
/// Every received export surfaces here channel-style so operators can
/// inspect traces / metrics / logs alongside their invokes.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 receives both <c>application/json</c> and the OTLP-default
/// <c>application/x-protobuf</c>: JSON is forwarded verbatim, protobuf
/// is captured as base64 with the wire-level metadata. Phase 2 swaps
/// the base64 branch for an inline decode via vendored
/// opentelemetry-proto descriptors.
/// </para>
/// <para>
/// The plugin doesn't make outbound calls — <c>serverUrl</c> is read
/// only as the discovery anchor for the workbench's per-URL grouping.
/// <see cref="InvokeAsync"/> returns the most recent envelope of the
/// requested kind; <see cref="InvokeStreamAsync"/> subscribes to the
/// envelope-store publish/subscribe channel and yields each export as
/// it arrives.
/// </para>
/// </remarks>
public sealed class BowireOtlpProtocol : IBowireProtocol
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    private OtlpEnvelopeStore? _store;

    public string Name => "OTLP";
    public string Description => "OpenTelemetry Protocol passive listener — receive traces / metrics / logs from services under test.";
    public string Id => "otlp";

    // Stylised "OTLP" mark — a hexagonal node + three radiating
    // arrows for the three signals (traces / metrics / logs).
    // Lucide-style stroke work matches the rest of the rail.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" aria-hidden="true"><polygon points="12 3 19 7.5 19 16.5 12 21 5 16.5 5 7.5 12 3"/><path d="M12 12V3"/><path d="M12 12l7 4.5"/><path d="M12 12l-7 4.5"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        // Embedded hosts resolve the singleton store off their DI
        // container. Standalone Tool also does (BowireOtlpReceiver is
        // registered via AddBowireOtlpReceiver during host setup), so
        // both deployment shapes reach the same buffer the receiver
        // endpoints are filling.
        _store = serviceProvider?.GetService(typeof(OtlpEnvelopeStore)) as OtlpEnvelopeStore;
    }

    /// <summary>
    /// Discovery surfaces a single virtual service <c>OtlpReceiver</c>
    /// with three methods — one per OTLP signal. Each method is marked
    /// <c>ServerStreaming</c> so the workbench's channel UX picks the
    /// right shape (receive-only stream of envelopes).
    /// </summary>
    public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // Empty server-url still returns the receiver — discovery in
        // passive-listener mode is intrinsic to the plugin, not tied
        // to a remote endpoint. The serverUrl is recorded so the
        // workbench groups OTLP under the right "URL bucket" alongside
        // any active-call protocols on the same host.
        var input = new BowireMessageInfo("OtlpRequest", "opentelemetry.proto.collector.v1.ExportRequest", []);
        var output = new BowireMessageInfo("OtlpEnvelope", "Kuestenlogik.Bowire.Protocol.Otlp.OtlpEnvelope", []);

        var methods = new List<BowireMethodInfo>
        {
            MakeMethod("ReceiveTraces",  "POST", "/v1/traces",  input, output),
            MakeMethod("ReceiveMetrics", "POST", "/v1/metrics", input, output),
            MakeMethod("ReceiveLogs",    "POST", "/v1/logs",    input, output),
        };

        var service = new BowireServiceInfo("OtlpReceiver", "opentelemetry.proto.collector.v1", methods)
        {
            Source = "otlp-listener",
            Description = "Passive OTLP HTTP receiver — exporters POST to /v1/{traces,metrics,logs} and the captured envelopes appear in this channel.",
            Version = "1.5.0",
            OriginUrl = string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl,
        };

        return Task.FromResult(new List<BowireServiceInfo> { service });
    }

    /// <summary>
    /// Returns the most recently received envelope of the kind named
    /// by <paramref name="method"/>. Treat this as "snapshot the
    /// current state" — for live streaming use
    /// <see cref="InvokeStreamAsync"/>.
    /// </summary>
    public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var kind = MapMethodToKind(method);
        if (kind is null)
        {
            return Task.FromResult(new InvokeResult(
                Response:   $"{{\"error\":\"Unknown OTLP method '{method}'. Expected ReceiveTraces / ReceiveMetrics / ReceiveLogs.\"}}",
                DurationMs: 0,
                Status:     "BadRequest",
                Metadata:   []));
        }

        var store = _store;
        if (store is null)
        {
            return Task.FromResult(new InvokeResult(
                Response:   "{\"error\":\"OTLP receiver not registered — call services.AddBowireOtlpReceiver() in the host.\"}",
                DurationMs: 0,
                Status:     "FailedPrecondition",
                Metadata:   []));
        }

        var latest = store.Latest(kind.Value);
        if (latest is null)
        {
            return Task.FromResult(new InvokeResult(
                Response:   $"{{\"status\":\"empty\",\"detail\":\"No {kind} envelopes received yet.\"}}",
                DurationMs: 0,
                Status:     "OK",
                Metadata:   []));
        }

        return Task.FromResult(new InvokeResult(
            Response:   EnvelopeToJson(latest),
            DurationMs: 0,
            Status:     "OK",
            Metadata:   []));
    }

    /// <summary>
    /// Subscribes to the envelope-store publish/subscribe channel and
    /// yields each newly received envelope (filtered by signal kind)
    /// as a JSON string. The stream stays open until <paramref name="ct"/>
    /// is cancelled.
    /// </summary>
    public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var kind = MapMethodToKind(method);
        if (kind is null)
        {
            yield return $"{{\"error\":\"Unknown OTLP method '{method}'. Expected ReceiveTraces / ReceiveMetrics / ReceiveLogs.\"}}";
            yield break;
        }

        var store = _store;
        if (store is null)
        {
            yield return "{\"error\":\"OTLP receiver not registered — call services.AddBowireOtlpReceiver() in the host.\"}";
            yield break;
        }

        // Surface any envelopes already in the ring as historical
        // context so the channel doesn't open empty after the
        // subscriber attached late. Order preserved (oldest first).
        foreach (var envelope in store.Snapshot(kind.Value))
        {
            yield return EnvelopeToJson(envelope);
        }

        await foreach (var envelope in store.SubscribeAsync(ct).ConfigureAwait(false))
        {
            if (envelope.Kind != kind.Value) continue;
            yield return EnvelopeToJson(envelope);
        }
    }

    /// <summary>
    /// OTLP is receive-only — there's no bidirectional channel to
    /// open. The workbench's stream surface
    /// (<see cref="InvokeStreamAsync"/>) covers the live-tail case.
    /// </summary>
    public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    internal static BowireMethodInfo MakeMethod(string name, string verb, string path, BowireMessageInfo input, BowireMessageInfo output) =>
        new(
            Name: name,
            FullName: $"opentelemetry.proto.collector.v1.OtlpReceiver/{name}",
            ClientStreaming: false,
            ServerStreaming: true,
            InputType: input,
            OutputType: output,
            MethodType: "ServerStreaming")
        {
            HttpMethod = verb,
            HttpPath   = path,
            // name starts with "Receive" → strip prefix, keep the
            // proper-cased signal (Traces / Metrics / Logs) in the
            // human copy. CA1308 frowns on ToLowerInvariant in
            // identifier-rendering paths; keeping the source case is
            // both correct and analyser-quiet.
            Summary    = $"OTLP {name[7..]} receiver endpoint.",
            Description = $"Exporters POST OTLP-encoded {name[7..]} to this path. JSON content-types are forwarded verbatim; protobuf is captured as base64 (Phase 1) — Phase 2 wires the canonical opentelemetry-proto descriptors for inline decode.",
            Deprecated = false,
        };

    internal static OtlpSignalKind? MapMethodToKind(string method)
    {
        if (string.IsNullOrEmpty(method)) return null;
        // Both bare method name and FullName tail are accepted so the
        // workbench's "fully-qualified method id" path works the same
        // as the bare name from a CLI invocation.
        var tail = method;
        var slash = method.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < method.Length) tail = method[(slash + 1)..];

        return tail switch
        {
            "ReceiveTraces"  => OtlpSignalKind.Traces,
            "ReceiveMetrics" => OtlpSignalKind.Metrics,
            "ReceiveLogs"    => OtlpSignalKind.Logs,
            _ => null,
        };
    }

    internal static string EnvelopeToJson(OtlpEnvelope envelope)
    {
        // Stable, indented JSON — the workbench renders this directly
        // in the channel pane. Keeps protobuf bodies as base64 so a
        // future Phase-2 decoder can layer over without changing the
        // wire shape that recordings will capture.
        return JsonSerializer.Serialize(new
        {
            kind        = envelope.Kind.ToString(),
            receivedAt  = envelope.ReceivedAt,
            contentType = envelope.ContentType,
            bodyBytes   = envelope.BodyBytes,
            remoteIp    = envelope.RemoteIp,
            bodyJson    = envelope.BodyJson,
            bodyBase64  = envelope.BodyBase64,
        }, s_indentedJson);
    }
}

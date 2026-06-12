// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// Bowire protocol plugin for NATS (nats.io) servers. Built on the
/// official <c>NATS.Net</c> client.
/// </summary>
/// <remarks>
/// <para>
/// Discovery model: the plugin subscribes to the wildcard subject
/// <c>&gt;</c> for a short window and groups whatever flows past by
/// first-token prefix into <see cref="BowireServiceInfo"/> entries
/// (see <see cref="NatsDiscovery"/>). Each subject gets three methods:
/// Publish (Unary), Subscribe (ServerStreaming), Request (Unary
/// req/reply).
/// </para>
/// <para>
/// Payload handling follows the same JSON → UTF-8 → hex fallback
/// chain Bowire uses for MQTT (<see cref="NatsPayloadHelper"/>).
/// </para>
/// <para>
/// Out of scope for Phase 1: JetStream consumers/streams, NATS
/// Services API, KV/object stores. Those land in a follow-up once
/// the pub/sub surface is settled.
/// </para>
/// </remarks>
public sealed class BowireNatsProtocol : IBowireProtocol
{
    public string Name => "NATS";
    public string Description => "NATS Core publish/subscribe + request/reply over the NATS protocol.";
    public string Id => "nats";

    // Official nats.io logo (rounded N glyph, brand colour).
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="#27AAE1" width="16" height="16" aria-hidden="true"><path d="M2.5 4.5h4.2l9.6 12V4.5h4.7v15h-4.2l-9.6-12v12H2.5z"/></svg>""";

    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new("autoInterpretJson", "Auto-interpret JSON",
            "Parse JSON payloads for structured display instead of raw text",
            "bool", true),
        new("scanDuration", "Subject scan duration",
            "How long to subscribe to '>' during discovery (seconds)",
            "number", 3),
    ];

    public void Initialize(IServiceProvider? serviceProvider) { }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var normalised = NatsConnectionHelper.NormaliseServerUrl(serverUrl);
        if (normalised is null) return [];

        // Three discovery sources run against the same connection:
        //   1. Subject scan via the '>' wildcard (Phase 1 — passive
        //      observation of whatever flows past).
        //   2. JetStream streams + consumers (server has '-js' on).
        //   3. $SRV.PING broadcast — NATS Services API.
        // Each source is best-effort; failure on one doesn't kill
        // the others.
        await using NatsConnection conn = new(NatsConnectionHelper.BuildOptions(normalised));
        try
        {
            await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        var all = new List<BowireServiceInfo>();

        // 1) Subject sample (Phase 1 shape, kept verbatim).
        try
        {
            var subjects = await NatsDiscovery.ScanSubjectsOnConnectionAsync(conn, ct).ConfigureAwait(false);
            all.AddRange(NatsDiscovery.BuildServices(subjects, serverUrl));
        }
        catch { /* swallow — see method docs */ }

        // 2) JetStream streams (only present when '-js' is enabled).
        try
        {
            all.AddRange(await NatsJetStreamDiscovery.ListAsync(conn, serverUrl, ct).ConfigureAwait(false));
        }
        catch { /* swallow */ }

        // 3) NATS Services API ($SRV.PING).
        try
        {
            all.AddRange(await NatsServicesDiscovery.ListAsync(conn, serverUrl, ct).ConfigureAwait(false));
        }
        catch { /* swallow */ }

        return all;
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var normalised = NatsConnectionHelper.NormaliseServerUrl(serverUrl);
        if (normalised is null)
            return new InvokeResult(null, 0, "Invalid NATS server URL", new());

        // FullName carries the operation kind so we can split publish vs
        // request without inventing a separate "service name" convention.
        // Three families:
        //   nats/<subject>/<publish|request>           — Phase 1 core
        //   nats/jetstream/<stream>/<info|consume>     — JetStream meta
        //   nats/jetstream/<stream>/publish/<subject>  — JS-acked publish
        //   nats/services/<service>/<endpoint>         — $SRV.* req/reply
        var route = ParseRoute(method);
        if (route.Family == NatsRouteFamily.JetStream)
            return await InvokeJetStreamAsync(normalised, route, jsonMessages, ct).ConfigureAwait(false);
        if (route.Family == NatsRouteFamily.Services)
            return await InvokeServiceAsync(normalised, route, jsonMessages, ct).ConfigureAwait(false);

        var (subject, op) = (route.Subject, route.Op);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payloadBytes = Encoding.UTF8.GetBytes(jsonMessages.FirstOrDefault() ?? "{}");

        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalised));
        try
        {
            await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);

            switch (op)
            {
                case "request":
                {
                    // RequestAsync round-trips: publish + wait for one
                    // inbox reply. CommandTimeout on the NatsOpts caps
                    // the wait window so a missing responder doesn't
                    // hang the UI.
                    var reply = await conn.RequestAsync<byte[], byte[]>(
                        subject, payloadBytes, cancellationToken: ct).ConfigureAwait(false);
                    sw.Stop();
                    var replyBytes = reply.Data ?? [];
                    return new InvokeResult(
                        NatsPayloadHelper.PayloadToDisplayString(replyBytes),
                        sw.ElapsedMilliseconds,
                        "OK",
                        new Dictionary<string, string>
                        {
                            ["subject"] = subject,
                            ["bytes"] = replyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["reply_to"] = reply.ReplyTo ?? "",
                        });
                }
                default:
                {
                    // Plain publish — fire-and-forget, no ack from the
                    // server. We surface the sent payload as the
                    // response so the UI has something to show.
                    string? replyTo = null;
                    if (metadata?.TryGetValue("reply_to", out var rt) == true
                        && !string.IsNullOrWhiteSpace(rt))
                    {
                        replyTo = rt;
                    }
                    await conn.PublishAsync<byte[]>(subject, payloadBytes, replyTo: replyTo, cancellationToken: ct)
                        .ConfigureAwait(false);
                    sw.Stop();
                    return new InvokeResult(
                        JsonSerializer.Serialize(new
                        {
                            subject,
                            payload = jsonMessages.FirstOrDefault() ?? "",
                            reply_to = replyTo,
                        }),
                        sw.ElapsedMilliseconds,
                        "OK",
                        new Dictionary<string, string>
                        {
                            ["subject"] = subject,
                            ["bytes"] = payloadBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, ex.Message, new());
        }
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalised = NatsConnectionHelper.NormaliseServerUrl(serverUrl);
        if (normalised is null) yield break;

        var route = ParseRoute(method);
        if (route.Family == NatsRouteFamily.JetStream && route.Op == "consume")
        {
            await foreach (var msg in StreamJetStreamConsumerAsync(normalised, route, ct).ConfigureAwait(false))
                yield return msg;
            yield break;
        }

        var subject = route.Subject;

        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalised));
        await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);

        // Queue group hint: when set in metadata, the subscribe joins
        // a queue group so messages on the subject are distributed
        // across all members (NATS-side load balancing) instead of
        // every member receiving every message.
        var queueGroup = metadata?.TryGetValue("queue_group", out var qg) == true
            && !string.IsNullOrWhiteSpace(qg) ? qg : null;

        // NATS.Net's SubscribeAsync returns an IAsyncEnumerable that
        // ends when ct cancels — no manual unsubscribe wiring needed.
        await foreach (var msg in conn
            .SubscribeAsync<byte[]>(subject, queueGroup: queueGroup, cancellationToken: ct)
            .ConfigureAwait(false))
        {
            var bytes = msg.Data ?? [];
            yield return JsonSerializer.Serialize(new
            {
                subject = msg.Subject,
                payload = NatsPayloadHelper.PayloadToDisplayString(bytes),
                reply_to = msg.ReplyTo,
                bytes = bytes.Length,
            });
        }
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // Phase 1: no channel surface. Pub/sub + request/reply already
        // cover the workbench's invoke buttons; an interactive duplex
        // channel against NATS would need a request-many or queue-group
        // story which Phase 2 settles together with JetStream.
        return Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>
    /// Split a discovery-time FullName like <c>nats/orders.created/request</c>
    /// into <c>(orders.created, request)</c>. Plain subject names — what
    /// the user types directly — default to publish.
    /// </summary>
    private static (string Subject, string Op) ResolveSubjectAndOp(string method)
    {
        if (string.IsNullOrEmpty(method)) return ("", "publish");
        var slash = method.LastIndexOf('/');
        if (slash < 0) return (method, "publish");

        var op = method[(slash + 1)..];
        var rest = method[..slash];

        // FullName starts with "nats/" — strip it. If the prefix is
        // missing (e.g. a hand-typed "subject/publish"), keep the bare
        // subject so the user's intent still goes through.
        if (rest.StartsWith("nats/", StringComparison.Ordinal))
            rest = rest["nats/".Length..];

        return (rest, op);
    }

    // ----- Phase 2 routing ---------------------------------------------

    private enum NatsRouteFamily { Core, JetStream, Services }

    private readonly record struct NatsRoute(
        NatsRouteFamily Family,
        string Subject,
        string Op,
        string? StreamName,
        string? ServiceName);

    /// <summary>
    /// Parse a discovery-time FullName into a route descriptor. Three
    /// families distinguished by the second segment:
    /// <c>nats/jetstream/&lt;stream&gt;/...</c>,
    /// <c>nats/services/&lt;service&gt;/...</c>, otherwise core.
    /// Falls back to the Phase-1 (subject, op) split for everything
    /// that doesn't match — including bare subjects the user typed.
    /// </summary>
    private static NatsRoute ParseRoute(string method)
    {
        if (!string.IsNullOrEmpty(method) && method.StartsWith("nats/jetstream/", StringComparison.Ordinal))
        {
            // nats/jetstream/<stream>/info
            // nats/jetstream/<stream>/consume
            // nats/jetstream/<stream>/publish/<subject>
            var rest = method["nats/jetstream/".Length..];
            var firstSlash = rest.IndexOf('/', StringComparison.Ordinal);
            if (firstSlash > 0)
            {
                var stream = rest[..firstSlash];
                var tail = rest[(firstSlash + 1)..];
                if (tail.StartsWith("publish/", StringComparison.Ordinal))
                {
                    var subject = tail["publish/".Length..];
                    return new NatsRoute(NatsRouteFamily.JetStream, subject, "publish", stream, null);
                }
                return new NatsRoute(NatsRouteFamily.JetStream, "", tail, stream, null);
            }
        }

        if (!string.IsNullOrEmpty(method) && method.StartsWith("nats/services/", StringComparison.Ordinal))
        {
            // nats/services/<service>/<endpoint-name>
            var rest = method["nats/services/".Length..];
            var firstSlash = rest.IndexOf('/', StringComparison.Ordinal);
            if (firstSlash > 0)
            {
                var serviceName = rest[..firstSlash];
                var endpoint = rest[(firstSlash + 1)..];
                return new NatsRoute(NatsRouteFamily.Services, endpoint, "request", null, serviceName);
            }
        }

        var (subj, op) = ResolveSubjectAndOp(method);
        return new NatsRoute(NatsRouteFamily.Core, subj, op, null, null);
    }

    // ----- JetStream invocation ----------------------------------------

    private static async Task<InvokeResult> InvokeJetStreamAsync(
        string normalisedUrl, NatsRoute route, List<string> jsonMessages, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalisedUrl));
        try
        {
            await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            var js = conn.CreateJetStreamContext();

            switch (route.Op)
            {
                case "info":
                {
                    var stream = await js.GetStreamAsync(route.StreamName!, cancellationToken: ct).ConfigureAwait(false);
                    var info = stream.Info;
                    sw.Stop();
                    return new InvokeResult(
                        JsonSerializer.Serialize(new
                        {
                            name = info.Config?.Name,
                            subjects = info.Config?.Subjects,
                            messages = info.State?.Messages ?? 0,
                            bytes = info.State?.Bytes ?? 0,
                            first_seq = info.State?.FirstSeq ?? 0,
                            last_seq = info.State?.LastSeq ?? 0,
                        }),
                        sw.ElapsedMilliseconds,
                        "OK",
                        new Dictionary<string, string>
                        {
                            ["stream"] = route.StreamName ?? "",
                        });
                }
                case "publish":
                {
                    var payloadBytes = Encoding.UTF8.GetBytes(jsonMessages.FirstOrDefault() ?? "{}");
                    var ack = await js.PublishAsync(route.Subject, payloadBytes, cancellationToken: ct)
                        .ConfigureAwait(false);
                    sw.Stop();
                    return new InvokeResult(
                        JsonSerializer.Serialize(new
                        {
                            stream = ack.Stream,
                            seq = ack.Seq,
                            duplicate = ack.Duplicate,
                        }),
                        sw.ElapsedMilliseconds,
                        "OK",
                        new Dictionary<string, string>
                        {
                            ["stream"] = ack.Stream ?? "",
                            ["seq"] = ack.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
                default:
                {
                    sw.Stop();
                    return new InvokeResult(null, sw.ElapsedMilliseconds,
                        $"Unknown JetStream operation '{route.Op}'", new());
                }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, ex.Message, new());
        }
    }

    private static async IAsyncEnumerable<string> StreamJetStreamConsumerAsync(
        string normalisedUrl, NatsRoute route,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalisedUrl));
        await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        var js = conn.CreateJetStreamContext();

        // Ordered consumer is the right shape for "show me what's
        // already on the stream" — no acks needed, no durable name
        // to leak, naturally cleans up when ct fires.
        var consumer = await js.CreateOrderedConsumerAsync(
            route.StreamName!, cancellationToken: ct).ConfigureAwait(false);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct).ConfigureAwait(false))
        {
            var bytes = msg.Data ?? [];
            yield return JsonSerializer.Serialize(new
            {
                subject = msg.Subject,
                payload = NatsPayloadHelper.PayloadToDisplayString(bytes),
                seq = msg.Metadata?.Sequence.Stream ?? 0UL,
                reply_to = msg.ReplyTo,
            });
        }
    }

    // ----- Services invocation -----------------------------------------

    private static async Task<InvokeResult> InvokeServiceAsync(
        string normalisedUrl, NatsRoute route, List<string> jsonMessages, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // For Services endpoints we don't have the actual subject
        // (it lives in the $SRV.INFO response we already consumed
        // during discovery). The closest direct invocation is the
        // PING fallback subject — same shape, no need to round-trip
        // discovery again.
        var subject = string.IsNullOrEmpty(route.Subject) || route.Subject == "ping"
            ? $"$SRV.PING.{route.ServiceName}"
            : route.Subject;

        var payloadBytes = Encoding.UTF8.GetBytes(jsonMessages.FirstOrDefault() ?? "{}");
        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalisedUrl));
        try
        {
            await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            var reply = await conn.RequestAsync<byte[], byte[]>(
                subject, payloadBytes, cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();
            var replyBytes = reply.Data ?? [];
            return new InvokeResult(
                NatsPayloadHelper.PayloadToDisplayString(replyBytes),
                sw.ElapsedMilliseconds,
                "OK",
                new Dictionary<string, string>
                {
                    ["service"] = route.ServiceName ?? "",
                    ["subject"] = subject,
                    ["bytes"] = replyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, ex.Message, new());
        }
    }
}

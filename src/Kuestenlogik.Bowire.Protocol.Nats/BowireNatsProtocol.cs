// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;

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

        try
        {
            var subjects = await NatsDiscovery.ScanSubjectsAsync(normalised, ct).ConfigureAwait(false);
            return NatsDiscovery.BuildServices(subjects, serverUrl);
        }
        catch
        {
            // Server unreachable / TLS mismatch / not actually NATS —
            // treat as "no discovery" the same way MQTT and JSON-RPC do.
            return [];
        }
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
        // method is the bare subject — the FullName looks like
        // "nats/<subject>/<op>" but we don't need the prefix here.
        var (subject, op) = ResolveSubjectAndOp(method);

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

        var (subject, _) = ResolveSubjectAndOp(method);

        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalised));
        await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);

        // NATS.Net's SubscribeAsync returns an IAsyncEnumerable that
        // ends when ct cancels — no manual unsubscribe wiring needed.
        await foreach (var msg in conn
            .SubscribeAsync<byte[]>(subject, cancellationToken: ct)
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
}

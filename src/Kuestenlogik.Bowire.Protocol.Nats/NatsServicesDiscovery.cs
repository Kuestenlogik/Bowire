// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// NATS Services API discovery via the <c>$SRV.*</c> broadcast
/// convention. Listens for replies to a broadcast <c>$SRV.PING</c>
/// for a short window, then follows up on each responder with a
/// <c>$SRV.INFO.&lt;name&gt;</c> request to enumerate the endpoints
/// (subjects) that service exposes.
/// </summary>
/// <remarks>
/// <para>
/// The NATS.Client.Services package only exposes the server-side
/// hosting API (<c>NatsSvcContext.AddServiceAsync</c>); discovery
/// is up to clients. The well-known protocol is documented at
/// <c>nats-io/nats.deno</c>: PING / INFO / STATS are broadcast on
/// the <c>$SRV.*</c> subject hierarchy and responses are JSON.
/// </para>
/// </remarks>
internal static class NatsServicesDiscovery
{
    /// <summary>
    /// Sweep the server for advertised services and turn each one
    /// into a <see cref="BowireServiceInfo"/> tagged
    /// <c>Service:&lt;name&gt;</c>. Each endpoint subject becomes a
    /// request method (Unary req/reply) on the service.
    /// </summary>
    public static async Task<List<BowireServiceInfo>> ListAsync(
        INatsConnection conn, string originUrl, CancellationToken ct,
        TimeSpan? scanDuration = null)
    {
        var duration = scanDuration ?? TimeSpan.FromSeconds(2);
        var pings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // PING phase: broadcast on $SRV.PING and collect every reply
        // for `duration`. The well-known reply payload is a JSON
        // object with at minimum { name, id, version }.
        var replyInbox = $"_INBOX.bowire.{Guid.NewGuid():N}"[..32];
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pingCts.CancelAfter(duration);

        var collectTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var reply in conn.SubscribeAsync<byte[]>(
                    replyInbox, cancellationToken: pingCts.Token).ConfigureAwait(false))
                {
                    if (reply.Data is null || reply.Data.Length == 0) continue;
                    try
                    {
                        var doc = JsonSerializer.Deserialize<JsonElement>(reply.Data);
                        if (doc.ValueKind == JsonValueKind.Object
                            && doc.TryGetProperty("name", out var nameEl)
                            && nameEl.ValueKind == JsonValueKind.String)
                        {
                            var key = doc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                ? $"{nameEl.GetString()}#{idEl.GetString()}"
                                : nameEl.GetString()!;
                            pings[key] = doc;
                        }
                    }
                    catch
                    {
                        // Not a service ping reply (or invalid JSON) — skip.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the scan window elapses.
            }
        }, ct);

        try
        {
            await conn.PublishAsync<byte[]>(
                "$SRV.PING", Array.Empty<byte>(),
                replyTo: replyInbox, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            // No connection / no permissions — bail with empty result.
            await pingCts.CancelAsync().ConfigureAwait(false);
            return [];
        }

        try { await collectTask.ConfigureAwait(false); }
        catch { /* swallow */ }

        if (pings.Count == 0) return [];

        // INFO phase: for each unique service name (drop the id
        // suffix), request $SRV.INFO.<name> once. Multiple instances
        // share the same endpoint set so one INFO is enough.
        var services = new List<BowireServiceInfo>();
        var uniqueNames = pings.Keys
            .Select(k => k.Contains('#', StringComparison.Ordinal) ? k[..k.IndexOf('#', StringComparison.Ordinal)] : k)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var name in uniqueNames)
        {
            JsonElement? infoDoc = null;
            try
            {
                var reply = await conn.RequestAsync<byte[], byte[]>(
                    $"$SRV.INFO.{name}", Array.Empty<byte>(),
                    cancellationToken: ct).ConfigureAwait(false);
                if (reply.Data is { Length: > 0 })
                {
                    infoDoc = JsonSerializer.Deserialize<JsonElement>(reply.Data);
                }
            }
            catch
            {
                // No responders / timeout — fall back to the PING-only
                // shape (one synthetic method per service).
            }

            services.Add(BuildServiceFromInfo(name, infoDoc, originUrl));
        }

        return services;
    }

    private static BowireServiceInfo BuildServiceFromInfo(
        string name, JsonElement? infoDoc, string originUrl)
    {
        var version = infoDoc?.TryGetProperty("version", out var verEl) == true
            && verEl.ValueKind == JsonValueKind.String
                ? verEl.GetString()
                : null;
        var description = version is null
            ? $"NATS service '{name}' (advertised via $SRV.PING)."
            : $"NATS service '{name}' v{version} (advertised via $SRV.PING).";

        var methods = new List<BowireMethodInfo>();
        if (infoDoc is { } doc
            && doc.TryGetProperty("endpoints", out var endpoints)
            && endpoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var endpoint in endpoints.EnumerateArray())
            {
                if (endpoint.ValueKind != JsonValueKind.Object) continue;
                var subject = endpoint.TryGetProperty("subject", out var subEl) && subEl.ValueKind == JsonValueKind.String
                    ? subEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(subject)) continue;

                var epName = endpoint.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? subject
                    : subject;
                var queueGroup = endpoint.TryGetProperty("queue_group", out var qgEl) && qgEl.ValueKind == JsonValueKind.String
                    ? qgEl.GetString()
                    : null;

                methods.Add(new BowireMethodInfo(
                    Name: epName,
                    FullName: $"nats/services/{name}/{epName}",
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: BuildRequestInput(),
                    OutputType: BuildReplyOutput(),
                    MethodType: "Unary")
                {
                    Summary = $"Request '{subject}' on service '{name}'",
                    Description = queueGroup is null
                        ? $"NATS Services endpoint — req/reply on subject '{subject}'."
                        : $"NATS Services endpoint — req/reply on '{subject}' (queue group: {queueGroup}).",
                });
            }
        }

        if (methods.Count == 0)
        {
            // No endpoint metadata — surface a single placeholder that
            // points at the well-known $SRV.PING address so the user
            // can at least probe the service from the UI.
            methods.Add(new BowireMethodInfo(
                Name: "ping",
                FullName: $"nats/services/{name}/ping",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: BuildRequestInput(),
                OutputType: BuildReplyOutput(),
                MethodType: "Unary")
            {
                Summary = $"PING service '{name}'",
                Description = "Sends $SRV.PING.<name> and returns the JSON ping response.",
            });
        }

        return new BowireServiceInfo($"Service:{name}", "nats", methods)
        {
            Source = "nats",
            OriginUrl = originUrl,
            Description = description,
        };
    }

    private static BowireMessageInfo BuildRequestInput() => new(
        "ServiceRequest",
        "nats.services.Request",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Request payload (JSON or plain text, sent as UTF-8).",
                Required = true,
            },
        ]);

    private static BowireMessageInfo BuildReplyOutput() => new(
        "ServiceReply",
        "nats.services.Reply",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("bytes", 2, "int32", "LABEL_OPTIONAL", false, false, null, null),
        ]);
}

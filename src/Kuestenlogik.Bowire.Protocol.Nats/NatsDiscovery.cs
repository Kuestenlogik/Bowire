// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// NATS subject discovery. NATS — unlike MQTT — has no "list all
/// retained topics" call, so we sample by subscribing to the
/// wildcard <c>&gt;</c> ("everything") for a short window and
/// collecting whatever flows past. Quiet subjects stay invisible;
/// the user can still call them by name if they know the subject.
/// </summary>
/// <remarks>
/// <para>
/// Each unique subject becomes three methods on the service the
/// subject groups under:
/// </para>
/// <list type="bullet">
///   <item><description><b>Publish</b> (Unary) — fire-and-forget on the subject.</description></item>
///   <item><description><b>Subscribe</b> (ServerStreaming) — observe future messages.</description></item>
///   <item><description><b>Request</b> (Unary) — req/reply round-trip; waits for one response.</description></item>
/// </list>
/// </remarks>
internal static class NatsDiscovery
{
    /// <summary>
    /// Scan the server for active subjects. Subscribes to <c>&gt;</c>
    /// (NATS wildcard for "all subjects under root") for up to
    /// <paramref name="scanDuration"/> and collects every subject that
    /// publishes at least one message during the window.
    /// </summary>
    public static async Task<HashSet<string>> ScanSubjectsAsync(
        string normalisedUrl, CancellationToken ct, TimeSpan? scanDuration = null)
    {
        var duration = scanDuration ?? TimeSpan.FromSeconds(3);
        var subjects = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = new NatsConnection(NatsConnectionHelper.BuildOptions(normalisedUrl));
        await conn.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(duration);

        try
        {
            // NatsConnection.SubscribeAsync yields NatsMsg<T> objects;
            // we only care about the Subject string here, the payload
            // bytes get dropped because BuildServices doesn't need
            // sample data — just the namespace shape.
            await foreach (var msg in conn.SubscribeAsync<byte[]>(">", cancellationToken: scanCts.Token)
                                .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(msg.Subject) && !msg.Subject.StartsWith('$'))
                    subjects.Add(msg.Subject);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: scan window elapsed or the outer caller
            // cancelled. Return whatever we've gathered.
        }

        return subjects;
    }

    /// <summary>
    /// Group discovered subjects into Bowire services. Each first
    /// token (segment before the first dot) becomes a service; the
    /// full subject becomes the method name. Single-token subjects
    /// (e.g. <c>health</c>) go into a <c>(root)</c> service so the UI
    /// always renders them under a header.
    /// </summary>
    public static List<BowireServiceInfo> BuildServices(HashSet<string> subjects, string originUrl)
    {
        if (subjects.Count == 0) return [];

        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var subject in subjects.OrderBy(s => s, StringComparer.Ordinal))
        {
            var dotIdx = subject.IndexOf('.');
            var prefix = dotIdx > 0 ? subject[..dotIdx] : "(root)";
            if (!groups.TryGetValue(prefix, out var list))
            {
                list = [];
                groups[prefix] = list;
            }
            list.Add(subject);
        }

        var services = new List<BowireServiceInfo>();
        foreach (var (prefix, subjectList) in groups)
        {
            var methods = new List<BowireMethodInfo>(capacity: subjectList.Count * 3);
            foreach (var subject in subjectList)
            {
                // Subscribe — server-streaming.
                methods.Add(new BowireMethodInfo(
                    Name: subject,
                    FullName: $"nats/{subject}/subscribe",
                    ClientStreaming: false,
                    ServerStreaming: true,
                    InputType: BuildSubscribeInput(),
                    OutputType: BuildMessageOutput(),
                    MethodType: "ServerStreaming")
                {
                    Summary = $"Subscribe to {subject}",
                    Description = $"Subscribes to NATS subject '{subject}'. Each message is streamed back as a JSON envelope (subject, payload, headers, byte count, optional reply subject)."
                });

                // Publish — fire-and-forget unary.
                methods.Add(new BowireMethodInfo(
                    Name: subject,
                    FullName: $"nats/{subject}/publish",
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: BuildPublishInput(),
                    OutputType: BuildPublishOutput(),
                    MethodType: "Unary")
                {
                    Summary = $"Publish to {subject}",
                    Description = $"Publishes a message to NATS subject '{subject}'. Set 'reply_to' or 'headers' via metadata."
                });

                // Request — req/reply unary.
                methods.Add(new BowireMethodInfo(
                    Name: subject,
                    FullName: $"nats/{subject}/request",
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: BuildPublishInput(),
                    OutputType: BuildMessageOutput(),
                    MethodType: "Unary")
                {
                    Summary = $"Request on {subject}",
                    Description = $"Issues a NATS request to '{subject}' and waits for one reply. Timeout is the plugin's CommandTimeout (10 s by default)."
                });
            }

            services.Add(new BowireServiceInfo(prefix, "nats", methods)
            {
                Source = "nats",
                OriginUrl = originUrl,
                Description = $"NATS subjects under '{prefix}.' ({subjectList.Count} subject{(subjectList.Count != 1 ? "s" : "")})",
            });
        }

        return services;
    }

    private static BowireMessageInfo BuildPublishInput() => new(
        "NatsPublishRequest",
        "nats.PublishRequest",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Message payload (JSON string or plain text). Sent as UTF-8 bytes.",
                Required = true,
            },
        ]);

    private static BowireMessageInfo BuildSubscribeInput() => new(
        "NatsSubscribeRequest",
        "nats.SubscribeRequest",
        []);

    private static BowireMessageInfo BuildPublishOutput() => new(
        "NatsPublishResponse",
        "nats.PublishResponse",
        [
            new BowireFieldInfo("subject", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("payload", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
        ]);

    private static BowireMessageInfo BuildMessageOutput() => new(
        "NatsMessage",
        "nats.Message",
        [
            new BowireFieldInfo("subject", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("payload", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("reply_to", 3, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("bytes", 4, "int32", "LABEL_OPTIONAL", false, false, null, null),
        ]);
}

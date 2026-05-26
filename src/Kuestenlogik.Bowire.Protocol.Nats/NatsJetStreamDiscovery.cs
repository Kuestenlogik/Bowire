// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// JetStream-side discovery: list the server's streams + the consumers
/// attached to each one. Surfaces each stream as a separate
/// <see cref="BowireServiceInfo"/> tagged with the
/// <c>JetStream:</c> prefix so the UI can group them away from the
/// subject sample the Phase-1 wildcard scanner produces.
/// </summary>
/// <remarks>
/// <para>
/// JetStream is opt-in on a NATS server — a vanilla nats-server
/// without the <c>-js</c> flag rejects every <c>$JS.*</c> request
/// with a 503. The discovery path swallows that and returns an
/// empty list so the rest of the plugin still works against pure-
/// core servers.
/// </para>
/// </remarks>
internal static class NatsJetStreamDiscovery
{
    /// <summary>
    /// Build one service per stream. Each stream gets two synthetic
    /// methods: <c>info</c> (Unary — returns stream + state JSON)
    /// and <c>consume</c> (ServerStreaming — pulls messages off an
    /// ordered consumer). Plus one method per filtered subject the
    /// stream listens on, so the user can publish directly into the
    /// stream from the form UI.
    /// </summary>
    public static async Task<List<BowireServiceInfo>> ListAsync(
        INatsConnection conn, string originUrl, CancellationToken ct)
    {
        var services = new List<BowireServiceInfo>();
        INatsJSContext js;
        try
        {
            js = conn.CreateJetStreamContext();
        }
        catch
        {
            // JetStream extension didn't load — should never happen
            // with the NATS.Net meta-package but cheap to guard.
            return services;
        }

        try
        {
            await foreach (var stream in js.ListStreamsAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                var info = stream.Info;
                var cfg = info.Config;
                var name = cfg?.Name ?? "(unnamed)";
                var subjects = cfg?.Subjects ?? new List<string>();
                var description =
                    $"JetStream stream '{name}' — {subjects.Count} subject filter(s), {info.State?.Messages ?? 0} message(s).";

                var methods = new List<BowireMethodInfo>
                {
                    new(
                        Name: "info",
                        FullName: $"nats/jetstream/{name}/info",
                        ClientStreaming: false,
                        ServerStreaming: false,
                        InputType: new BowireMessageInfo("Empty", "nats.Empty", []),
                        OutputType: BuildStreamInfoOutput(),
                        MethodType: "Unary")
                    {
                        Summary = $"Read stream metadata for '{name}'",
                        Description = "Returns the stream's configuration and current state (message count, byte total, first/last sequence) as a JSON document.",
                    },
                    new(
                        Name: "consume",
                        FullName: $"nats/jetstream/{name}/consume",
                        ClientStreaming: false,
                        ServerStreaming: true,
                        InputType: BuildConsumeInput(),
                        OutputType: BuildMessageOutput(),
                        MethodType: "ServerStreaming")
                    {
                        Summary = $"Consume messages from '{name}'",
                        Description = "Opens an ordered consumer and streams stored messages back. Each yield is a JSON envelope with subject, payload, sequence, and reply_to.",
                    },
                };

                // Publish-into-stream methods, one per filtered subject
                // listed on the stream config. A stream with no
                // subject filter (rare, source-only) skips this part.
                foreach (var subject in subjects)
                {
                    methods.Add(new BowireMethodInfo(
                        Name: subject,
                        FullName: $"nats/jetstream/{name}/publish/{subject}",
                        ClientStreaming: false,
                        ServerStreaming: false,
                        InputType: BuildPublishInput(),
                        OutputType: BuildPublishAckOutput(),
                        MethodType: "Unary")
                    {
                        Summary = $"Publish to '{subject}' (JetStream-acked)",
                        Description = "Publishes through JetStream so the server returns a PubAck (stream + sequence). Use this instead of core publish when you need durability.",
                    });
                }

                services.Add(new BowireServiceInfo($"JetStream:{name}", "nats", methods)
                {
                    Source = "nats",
                    OriginUrl = originUrl,
                    Description = description,
                });
            }
        }
        catch
        {
            // JetStream API not enabled on this server (no -js flag,
            // missing accounts permission, ...). Discovery is best-
            // effort; absence is fine.
        }

        return services;
    }

    private static BowireMessageInfo BuildPublishInput() => new(
        "JetStreamPublishRequest",
        "nats.jetstream.PublishRequest",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Message payload (JSON string or plain text). Sent as UTF-8 bytes.",
                Required = true,
            },
        ]);

    private static BowireMessageInfo BuildConsumeInput() => new(
        "JetStreamConsumeRequest",
        "nats.jetstream.ConsumeRequest",
        [
            new BowireFieldInfo("max_messages", 1, "int32", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Stop after N messages. Leave empty to consume until cancelled.",
            },
        ]);

    private static BowireMessageInfo BuildPublishAckOutput() => new(
        "JetStreamPubAck",
        "nats.jetstream.PubAck",
        [
            new BowireFieldInfo("stream", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("seq", 2, "int64", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("duplicate", 3, "bool", "LABEL_OPTIONAL", false, false, null, null),
        ]);

    private static BowireMessageInfo BuildStreamInfoOutput() => new(
        "JetStreamStreamInfo",
        "nats.jetstream.StreamInfo",
        [
            new BowireFieldInfo("name", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("subjects", 2, "string", "LABEL_OPTIONAL", false, true, null, null),
            new BowireFieldInfo("messages", 3, "int64", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("bytes", 4, "int64", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("first_seq", 5, "int64", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("last_seq", 6, "int64", "LABEL_OPTIONAL", false, false, null, null),
        ]);

    private static BowireMessageInfo BuildMessageOutput() => new(
        "JetStreamMessage",
        "nats.jetstream.Message",
        [
            new BowireFieldInfo("subject", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("payload", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("seq", 3, "int64", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("reply_to", 4, "string", "LABEL_OPTIONAL", false, false, null, null),
        ]);
}

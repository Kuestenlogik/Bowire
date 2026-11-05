// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// How a NATS call is entered by hand (#995). The method carries the whole
/// route the plugin parses — <c>&lt;subject&gt;/&lt;op&gt;</c> for core NATS,
/// <c>nats/jetstream/&lt;stream&gt;/…</c> for JetStream — so a request is not
/// mistaken for a publish the way a bare subject would be.
/// </summary>
internal static class NatsManualEntry
{
    private static readonly string[] s_coreKinds = ["publish", "request", "subscribe"];

    public static readonly BowireManualEntryForm Form = new(
    [
        new BowireManualCallKind("publish", "Publish", "Unary")
        {
            Service = "NATS",
            Method = "{subject}/publish",
            Description = "Publish one message; no reply is waited for.",
            BodyExample = "{\"orderId\": 42}",
        },
        new BowireManualCallKind("request", "Request", "Unary")
        {
            Service = "NATS",
            Method = "{subject}/request",
            Description = "Publish one message and wait for the reply.",
            BodyExample = "{\"orderId\": 42}",
        },
        new BowireManualCallKind("subscribe", "Subscribe", "ServerStreaming")
        {
            Service = "NATS",
            Method = "{subject}/subscribe",
            Description = "Follow the messages on the subject; wildcards (*, >) allowed.",
        },
        new BowireManualCallKind("js-publish", "JetStream publish", "Unary")
        {
            Service = "NATS",
            Method = "nats/jetstream/{stream}/publish/{subject}",
            Description = "Publish into a stream and wait for its acknowledgement.",
            BodyExample = "{\"orderId\": 42}",
        },
        new BowireManualCallKind("js-consume", "JetStream consume", "ServerStreaming")
        {
            Service = "NATS",
            Method = "nats/jetstream/{stream}/consume",
            Description = "Read a stream from its start through an ordered consumer.",
        },
    ])
    {
        UrlPlaceholder = "nats://localhost:4222",
        Fields =
        [
            new BowireManualField("subject", "Subject", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "orders.created",
                CallKinds = [.. s_coreKinds, "js-publish"],
            },
            new BowireManualField("stream", "Stream", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "ORDERS",
                CallKinds = ["js-publish", "js-consume"],
            },
            new BowireManualField("reply_to", "Reply to", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "orders.replies",
                Description = "The subject a subscriber answers on.",
                CallKinds = ["publish"],
            },
            new BowireManualField("queue_group", "Queue group", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "workers",
                Description = "Share the messages with the other members of the group.",
                CallKinds = ["subscribe"],
            },
        ],
    };
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Pulsar;

/// <summary>
/// How a Pulsar call is entered by hand (#995): the topic as the service and
/// <c>produce</c> or <c>subscribe</c> as the method, which
/// <see cref="BowirePulsarProtocol.ResolveMethodName"/> turns into the route
/// the invoke paths read.
/// </summary>
internal static class PulsarManualEntry
{
    public static readonly BowireManualEntryForm Form = new(
    [
        new BowireManualCallKind("produce", "Produce", "Unary")
        {
            Service = "{topic}",
            Method = "produce",
            Description = "Send one message to the topic.",
            BodyExample = "{\"orderId\": 42}",
        },
        new BowireManualCallKind("subscribe", "Subscribe", "ServerStreaming")
        {
            Service = "{topic}",
            Method = "subscribe",
            Description = "Follow the messages arriving on the topic.",
        },
    ])
    {
        UrlPlaceholder = "pulsar://localhost:6650",
        Fields =
        [
            new BowireManualField("topic", "Topic", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "persistent://public/default/orders",
                Description = "A short name is completed in the first configured namespace.",
            },
            new BowireManualField("subscription_name", "Subscription", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "bowire",
                CallKinds = ["subscribe"],
            },
            // A select, not a bool: empty has to mean "the plugin setting",
            // whose default is true, so a checkbox could never say false.
            new BowireManualField("from_latest", "Start at", BowireManualFieldPlacement.Metadata)
            {
                Type = "select",
                Options = [new("true", "Newest message"), new("false", "Earliest message kept")],
                Description = "Empty: the plugin's setting.",
                CallKinds = ["subscribe"],
            },
        ],
    };
}

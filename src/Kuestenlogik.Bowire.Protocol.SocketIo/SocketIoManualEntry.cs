// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.SocketIo;

/// <summary>
/// How a Socket.IO call is entered by hand (#995). An emit names its event
/// in the body (<c>{"event": …, "data": …}</c>); a listen follows every event,
/// or the one named as the method. The namespace travels as metadata.
/// </summary>
internal static class SocketIoManualEntry
{
    public static readonly BowireManualEntryForm Form = new(
    [
        new BowireManualCallKind("emit", "Emit", "Unary")
        {
            Service = "Socket.IO",
            Method = "emit",
            Description = "Emit one event and show what the server sends back within three seconds.",
            BodyExample = "{\"event\": \"chat\", \"data\": \"hello\"}",
        },
        new BowireManualCallKind("listen", "Listen to all events", "ServerStreaming")
        {
            Service = "Socket.IO",
            Method = "listen",
            Description = "Follow every event the server emits.",
        },
        new BowireManualCallKind("listen-event", "Listen to one event", "ServerStreaming")
        {
            Service = "Socket.IO",
            Method = "{event}",
            Description = "Follow one event.",
        },
    ])
    {
        UrlPlaceholder = "http://localhost:3000",
        Fields =
        [
            new BowireManualField("event", "Event", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "chat",
                CallKinds = ["listen-event"],
            },
            new BowireManualField(BowireSocketIoProtocol.NamespaceMetadataKey, "Namespace", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "/harbor",
                Description = "Empty: the default namespace, or the one in the URL's path.",
            },
        ],
    };
}

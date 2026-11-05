// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.SignalR;

/// <summary>
/// How a SignalR call is entered by hand (#995): the hub's path as the
/// service, the hub method as the method. Each message of the body is one
/// argument, as JSON.
/// </summary>
internal static class SignalRManualEntry
{
    public static readonly BowireManualEntryForm Form = new(
    [
        new BowireManualCallKind("invoke", "Invoke", "Unary")
        {
            Service = "{hub}",
            Method = "{hubMethod}",
            Description = "Call the hub method and read its result.",
            BodyExample = "\"hello\"",
        },
        new BowireManualCallKind("stream", "Stream", "ServerStreaming")
        {
            Service = "{hub}",
            Method = "{hubMethod}",
            Description = "Call a hub method that streams, and follow its items.",
            BodyExample = "10",
        },
        new BowireManualCallKind("channel", "Channel", "Duplex")
        {
            Service = "{hub}",
            Method = "{hubMethod}",
            Description = "Stream each message you send to the hub method, and follow what it streams back.",
            BodyExample = "\"hello\"",
        },
    ])
    {
        UrlPlaceholder = "http://localhost:5000",
        Fields =
        [
            new BowireManualField("hub", "Hub", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "/chathub",
                Description = "The hub's path; joined to the server URL unless that already ends with it.",
            },
            new BowireManualField("hubMethod", "Hub method", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "SendMessage",
            },
        ],
    };
}

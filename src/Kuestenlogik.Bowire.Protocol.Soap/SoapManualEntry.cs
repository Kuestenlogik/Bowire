// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Soap;

/// <summary>
/// How a SOAP call is entered by hand (#995): the operation, and the action,
/// namespace and version that the plugin reads from the call's metadata
/// (<see cref="BowireSoapProtocol"/>'s invoke). Without them a hand-entered
/// call reaches no service that dispatches on SOAPAction.
/// </summary>
internal static class SoapManualEntry
{
    public static readonly BowireManualEntryForm Form = new(
    [
        new BowireManualCallKind("call", "Call", "Unary")
        {
            Service = "SOAP",
            Method = "{operation}",
            Description = "Send one SOAP envelope and read the response.",
            BodyExample = "<a>1</a><b>2</b>",
        },
    ])
    {
        UrlPlaceholder = "https://example.com/Calculator.asmx",
        Fields =
        [
            new BowireManualField("operation", "Operation", BowireManualFieldPlacement.Template)
            {
                Required = true,
                Placeholder = "Add",
                Description = "The body element: the operation the envelope calls.",
            },
            new BowireManualField("soap_action", "SOAPAction", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "http://tempuri.org/Add",
                Description = "Sent as the SOAPAction header (SOAP 1.1) or the action parameter (SOAP 1.2).",
            },
            new BowireManualField("target_namespace", "Target namespace", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "http://tempuri.org/",
                Description = "The namespace of the operation element.",
            },
            new BowireManualField("soap_version", "SOAP version", BowireManualFieldPlacement.Metadata)
            {
                Type = "select",
                Options = [new("1.1", "1.1"), new("1.2", "1.2")],
                Description = "Empty: the workspace's default SOAP version.",
            },
            new BowireManualField("endpoint_url", "Endpoint URL", BowireManualFieldPlacement.Metadata)
            {
                Placeholder = "https://other-host.example.com/Calculator.asmx",
                Description = "Where the envelope goes, when that is not the server URL.",
            },
        ],
    };
}

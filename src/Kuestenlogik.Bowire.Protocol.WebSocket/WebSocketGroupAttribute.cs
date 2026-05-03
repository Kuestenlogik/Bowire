// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Groups one or more WebSocket endpoints into a named service in the Bowire sidebar.
/// Without this attribute endpoints land in a generic "WebSocket" service (or their own
/// DisplayName when only one endpoint is registered).
/// </summary>
/// <example>
/// <code>
/// // Inline (Minimal API) — one group per endpoint:
/// app.MapGet("/ws/send", sendHandler)
///    .WithMetadata(new WebSocketEndpointAttribute("Send"))
///    .WithMetadata(new WebSocketGroupAttribute("Chat"));
///
/// app.MapGet("/ws/receive", recvHandler)
///    .WithMetadata(new WebSocketEndpointAttribute("Receive"))
///    .WithMetadata(new WebSocketGroupAttribute("Chat"));
///
/// // Class-level — every endpoint defined inside inherits the group (read by
/// // WebSocketEndpointDiscovery via reflection on the delegate's declaring type):
/// [WebSocketGroup("Chat")]
/// public static class ChatEndpoints { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class WebSocketGroupAttribute : Attribute
{
    public WebSocketGroupAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Service/group name shown in the Bowire sidebar.</summary>
    public string Name { get; }
}

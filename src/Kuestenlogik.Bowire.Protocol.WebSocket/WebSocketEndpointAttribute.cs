// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Marks an ASP.NET Core endpoint as a WebSocket endpoint that Bowire
/// should expose in the sidebar. Use it on Minimal API delegates or
/// controller actions that perform <c>HttpContext.WebSockets.AcceptWebSocketAsync</c>.
/// </summary>
/// <example>
/// <code>
/// app.MapGet("/ws/echo", async (HttpContext ctx) =>
/// {
///     using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
///     // ... echo loop ...
/// })
/// .WithMetadata(new WebSocketEndpointAttribute("Echo", "Echoes every message back."));
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class WebSocketEndpointAttribute : Attribute
{
    public WebSocketEndpointAttribute(string? displayName = null, string? description = null)
    {
        DisplayName = displayName;
        Description = description;
    }

    public string? DisplayName { get; }
    public string? Description { get; }
}

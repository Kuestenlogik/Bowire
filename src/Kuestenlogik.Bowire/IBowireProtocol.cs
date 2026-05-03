// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Protocol plugin for Bowire. Implement this to add support for a new protocol.
/// Discovered automatically via assembly scanning.
/// </summary>
public interface IBowireProtocol
{
    /// <summary>Protocol name shown in UI tabs.</summary>
    string Name { get; }

    /// <summary>Short identifier (e.g., "grpc", "signalr").</summary>
    string Id { get; }

    /// <summary>SVG icon for the protocol tab.</summary>
    string IconSvg { get; }

    /// <summary>Called after registration to inject the app's service provider (embedded mode).</summary>
    void Initialize(IServiceProvider? serviceProvider) { }

    /// <summary>
    /// Settings schema this plugin contributes to the Settings dialog.
    /// Each entry becomes a toggle/input in the plugin's section.
    /// Default implementation returns empty (no plugin-specific settings).
    /// </summary>
    IReadOnlyList<BowirePluginSetting> Settings => [];

    /// <summary>Discover available services and methods.</summary>
    Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default);

    /// <summary>Invoke a unary or client-streaming call.</summary>
    Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>Invoke a server-streaming or duplex call.</summary>
    IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>Open an interactive channel (for duplex/client-streaming).</summary>
    Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);
}

/// <summary>
/// Interactive bidirectional channel for duplex/client-streaming protocols.
/// </summary>
public interface IBowireChannel : IAsyncDisposable
{
    /// <summary>Unique identifier for this channel.</summary>
    string Id { get; }

    /// <summary>Whether the client can stream messages.</summary>
    bool IsClientStreaming { get; }

    /// <summary>Whether the server can stream responses.</summary>
    bool IsServerStreaming { get; }

    /// <summary>Number of messages sent so far.</summary>
    int SentCount { get; }

    /// <summary>Whether the send side has been closed.</summary>
    bool IsClosed { get; }

    /// <summary>Elapsed time since channel was opened.</summary>
    long ElapsedMs { get; }

    /// <summary>
    /// Protocol-negotiated sub-protocol, if any. WebSocket exposes the
    /// value picked by the server's handshake response so the recorder
    /// can capture it and the mock can replay a matching upgrade
    /// negotiation. Channels that don't use sub-protocols (SignalR,
    /// MCP, Socket.IO) return <c>null</c>.
    /// </summary>
    string? NegotiatedSubProtocol => null;

    /// <summary>Send a message to the channel.</summary>
    Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default);

    /// <summary>Close the send side of the channel.</summary>
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>Read responses as they arrive.</summary>
    IAsyncEnumerable<string> ReadResponsesAsync(CancellationToken ct = default);
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Static lookup from a recording's lowercased protocol id
/// (<c>"grpc"</c>, <c>"signalr"</c>, <c>"socketio"</c>, …) to the
/// canonical NuGet package id (<c>"Kuestenlogik.Bowire.Protocol.Grpc"</c>, …).
/// <para>
/// Used by the <c>bowire mock</c> CLI to suggest install commands when
/// a recording references a protocol whose plugin isn't loaded, and by
/// the workbench's "missing plugin" modal for the same purpose.
/// </para>
/// <para>
/// The map is hardcoded on purpose: the whole reason we're guessing a
/// package id is that the plugin <em>isn't</em> installed yet, so we
/// can't read the manifest off disk. Adding a new first-party plugin
/// means appending one line here.
/// </para>
/// </summary>
public static class PluginPackageMap
{
    private static readonly Dictionary<string, string> s_byProtocolId = new(StringComparer.OrdinalIgnoreCase)
    {
        // First-party in-tree plugins — bundled with the standalone
        // CLI, but for the embedded path the user adds them per
        // protocol.
        ["grpc"]      = "Kuestenlogik.Bowire.Protocol.Grpc",
        ["rest"]      = "Kuestenlogik.Bowire.Protocol.Rest",
        ["graphql"]   = "Kuestenlogik.Bowire.Protocol.GraphQL",
        ["signalr"]   = "Kuestenlogik.Bowire.Protocol.SignalR",
        ["sse"]       = "Kuestenlogik.Bowire.Protocol.Sse",
        ["websocket"] = "Kuestenlogik.Bowire.Protocol.WebSocket",
        ["mcp"]       = "Kuestenlogik.Bowire.Protocol.Mcp",
        ["mqtt"]      = "Kuestenlogik.Bowire.Protocol.Mqtt",
        ["socketio"]  = "Kuestenlogik.Bowire.Protocol.SocketIo",
        ["odata"]     = "Kuestenlogik.Bowire.Protocol.OData",

        // Third-party / external plugins — sibling repos. Same
        // installer pipeline, separate release cadence.
        ["storm"]     = "Kuestenlogik.Bowire.Protocol.Storm",
        ["kafka"]     = "Kuestenlogik.Bowire.Protocol.Kafka",
        ["dis"]       = "Kuestenlogik.Bowire.Protocol.Dis",
        ["udp"]       = "Kuestenlogik.Bowire.Protocol.Udp",
    };

    /// <summary>
    /// Returns the NuGet package id Bowire ships under for the given
    /// protocol id, or <c>null</c> when the id isn't in Bowire's
    /// catalogue (a custom plugin we don't know about).
    /// </summary>
    public static string? TryGetPackageId(string protocolId)
    {
        if (string.IsNullOrWhiteSpace(protocolId)) return null;
        return s_byProtocolId.TryGetValue(protocolId, out var pkg) ? pkg : null;
    }

    /// <summary>
    /// Snapshot of the full protocol-id → package-id catalogue. Used
    /// by the workbench's <c>/api/plugins/protocols</c> endpoint so
    /// the client can offer install commands for protocols Bowire
    /// knows about without duplicating the hardcoded list on the JS
    /// side.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(s_byProtocolId, StringComparer.OrdinalIgnoreCase);

}

/// <summary>
/// Diagnostic shape for a missing plugin: the recording referenced
/// <see cref="ProtocolId"/>, but no <c>IBowireProtocol</c> with
/// that id is loaded. <see cref="SuggestedPackageId"/> is the
/// canonical NuGet package — null when the protocol isn't in
/// <see cref="PluginPackageMap"/>'s hardcoded catalogue (custom
/// plugin from a third party).
/// </summary>
public sealed record MissingPlugin(string ProtocolId, string? SuggestedPackageId);

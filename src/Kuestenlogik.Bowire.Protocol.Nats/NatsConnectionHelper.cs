// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using NATS.Client.Core;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// Shared helpers for NATS connection setup and server URL normalisation.
/// Mirrors the shape of <c>MqttConnectionHelper</c> so the two plugins
/// stay symmetric for code review.
/// </summary>
internal static class NatsConnectionHelper
{
    public const int DefaultPort = 4222;

    /// <summary>
    /// Normalise a user-entered server URL to NATS' <c>nats://host:port</c>
    /// form. Accepts plain <c>host:port</c>, <c>nats://</c>,
    /// <c>tls://</c>, <c>ws://</c>, <c>wss://</c>, and (for paste-from-
    /// elsewhere convenience) <c>http(s)://</c>. Returns null if the
    /// input doesn't look like a server URL we can connect to.
    /// </summary>
    public static string? NormaliseServerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var raw = url.Trim();

        // Already a NATS-flavoured scheme — pass through unchanged so
        // the client picks the right transport (TLS, WebSocket, etc.).
        foreach (var scheme in new[] { "nats://", "tls://", "ws://", "wss://" })
        {
            if (raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return raw;
        }

        // http(s) gets rewritten to nats:// — convenience for users
        // who paste the same URL they use for other Bowire protocols.
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "tls://" + raw["https://".Length..];
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "nats://" + raw["http://".Length..];

        // Bare host[:port].
        return raw.Contains(':', StringComparison.Ordinal)
            ? "nats://" + raw
            : $"nats://{raw}:{DefaultPort}";
    }

    public static NatsOpts BuildOptions(string serverUrl)
    {
        // CommandTimeout caps individual req/reply waits — Bowire's
        // default 10s lines up with the gRPC/REST plugins' UX
        // expectation. ConnectTimeout caps the TCP+TLS handshake.
        return NatsOpts.Default with
        {
            Url = serverUrl,
            Name = $"bowire-{Guid.NewGuid():N}"[..24],
            CommandTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
    }
}

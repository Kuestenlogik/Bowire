// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Pulsar;

/// <summary>
/// URL plumbing for the Pulsar plugin. The workbench accepts a few
/// different shapes (binary-protocol URL, HTTP admin URL, bare host)
/// and routes each to the right component: <c>DotPulsar</c> needs the
/// <c>pulsar://</c> broker URL, the admin REST surface needs an
/// <c>http(s)://</c> URL.
/// </summary>
internal static class PulsarConnectionHelper
{
    public const int DefaultBrokerPort = 6650;
    public const int DefaultBrokerTlsPort = 6651;
    public const int DefaultAdminPort = 8080;

    /// <summary>
    /// Split the user-supplied serverUrl into the broker URL DotPulsar
    /// connects with and the HTTP admin URL discovery uses. Returns
    /// <c>null</c> when the input can't be parsed at all.
    /// </summary>
    /// <remarks>
    /// Accepted shapes:
    /// <list type="bullet">
    ///   <item><c>pulsar://host:6650</c> — binary broker; admin guessed
    ///   at <c>http://host:8080</c>.</item>
    ///   <item><c>pulsar+ssl://host:6651</c> — TLS broker; admin
    ///   guessed at <c>https://host:8080</c>.</item>
    ///   <item><c>http://host:8080</c> — admin URL; broker guessed at
    ///   <c>pulsar://host:6650</c>.</item>
    ///   <item><c>https://host:8443</c> — TLS admin; broker guessed at
    ///   <c>pulsar+ssl://host:6651</c>.</item>
    ///   <item><c>host</c> or <c>host:port</c> — treated as
    ///   <c>pulsar://host:port</c>, default port 6650.</item>
    /// </list>
    /// </remarks>
    public static PulsarEndpoints? Resolve(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        var trimmed = serverUrl.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            switch (uri.Scheme)
            {
                case "pulsar":
                    return new PulsarEndpoints(
                        BrokerUrl: trimmed,
                        AdminBaseUrl: $"http://{uri.Host}:{DefaultAdminPort}",
                        UseTls: false);
                case "pulsar+ssl":
                    return new PulsarEndpoints(
                        BrokerUrl: trimmed,
                        AdminBaseUrl: $"https://{uri.Host}:{DefaultAdminPort}",
                        UseTls: true);
                case "http":
                    return new PulsarEndpoints(
                        BrokerUrl: $"pulsar://{uri.Host}:{DefaultBrokerPort}",
                        AdminBaseUrl: $"http://{uri.Host}:{(uri.IsDefaultPort ? DefaultAdminPort : uri.Port)}",
                        UseTls: false);
                case "https":
                    return new PulsarEndpoints(
                        BrokerUrl: $"pulsar+ssl://{uri.Host}:{DefaultBrokerTlsPort}",
                        AdminBaseUrl: $"https://{uri.Host}:{(uri.IsDefaultPort ? DefaultAdminPort : uri.Port)}",
                        UseTls: true);
            }
        }

        // Bare host[:port] — no scheme. Treat as plain-text broker.
        // Pulsar's binary protocol is the more useful default than admin
        // because most production deployments lock the admin port down.
        var hostPart = trimmed;
        var port = DefaultBrokerPort;
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && int.TryParse(trimmed[(colon + 1)..], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var p))
        {
            hostPart = trimmed[..colon];
            port = p;
        }
        if (string.IsNullOrEmpty(hostPart) || hostPart.Contains('/', StringComparison.Ordinal))
            return null;

        return new PulsarEndpoints(
            BrokerUrl: $"pulsar://{hostPart}:{port}",
            AdminBaseUrl: $"http://{hostPart}:{DefaultAdminPort}",
            UseTls: false);
    }

    /// <summary>
    /// Expand a short topic name to its fully-qualified form. Pulsar's
    /// convention: <c>my-topic</c> → <c>persistent://public/default/my-topic</c>.
    /// Names that already start with <c>persistent://</c> or
    /// <c>non-persistent://</c> pass through unchanged.
    /// </summary>
    public static string NormaliseTopicName(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "";
        if (topic.StartsWith("persistent://", StringComparison.Ordinal)
            || topic.StartsWith("non-persistent://", StringComparison.Ordinal))
        {
            return topic;
        }
        // Short form — assume public/default like the Pulsar CLI does.
        // Slashes in the middle mean "tenant/ns/name" already.
        var slashes = topic.Count(c => c == '/');
        return slashes switch
        {
            2 => "persistent://" + topic,
            _ => "persistent://public/default/" + topic,
        };
    }
}

/// <summary>
/// Resolved Pulsar endpoint pair — broker (binary protocol) +
/// admin (HTTP).
/// </summary>
internal sealed record PulsarEndpoints(string BrokerUrl, string AdminBaseUrl, bool UseTls);

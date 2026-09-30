// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// Says so when a call goes direct although a proxy is configured for its
/// host (#680) — the case where a plugin's transport cannot use the proxy.
/// Once per plugin and host, so a stream of calls does not flood the log.
/// </summary>
public static partial class BowireProxyNotice
{
    private static readonly ConcurrentDictionary<string, byte> s_reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The notice for a call through <paramref name="protocol"/> to
    /// <paramref name="serverUrl"/>, or <c>null</c> when the call follows the
    /// proxy (or no proxy applies to the host).
    /// </summary>
    public static string? For(IBowireProtocol protocol, string? serverUrl)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var support = protocol is IBowireProxySupport s ? s.ProxySupport : BowireProxySupport.Unknown;
        if (support is BowireProxySupport.Full or BowireProxySupport.NotApplicable) return null;
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(ToUri(serverUrl), UriKind.Absolute, out var target)) return null;

        var proxy = BowireNetworkPolicy.Current.ProxyFor(target);
        if (proxy is null) return null;

        var note = protocol is IBowireProxySupport n && n.ProxyNote is { } text
            ? text
            : "The plugin does not declare whether it can use a proxy.";
        return $"{protocol.Name}: a proxy ({proxy.Host}:{proxy.Port}) is configured for {target.Host}, "
            + $"but this connection may go direct. {note}";
    }

    /// <summary>Log <see cref="For"/> once per plugin and host.</summary>
    public static void Report(IBowireProtocol protocol, string? serverUrl, HttpContext? context)
    {
        var notice = For(protocol, serverUrl);
        if (notice is null) return;
        if (!s_reported.TryAdd(protocol.Id + "|" + serverUrl, 0)) return;
        var logger = context?.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Kuestenlogik.Bowire.Network");
        if (logger is not null) LogDirect(logger, notice);
    }

    // Broker URLs come as host:port or mqtt://… — give a scheme-less one a
    // placeholder scheme so the host can be read.
    private static string ToUri(string serverUrl) =>
        serverUrl.Contains("://", StringComparison.Ordinal) ? serverUrl : "tcp://" + serverUrl;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Notice}")]
    private static partial void LogDirect(ILogger logger, string notice);
}

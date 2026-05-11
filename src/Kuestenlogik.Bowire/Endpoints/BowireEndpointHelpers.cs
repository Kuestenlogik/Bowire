// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Shared state and helpers used by every <c>Bowire*Endpoints</c>
/// extension class. Lifted out of the old monolithic
/// <see cref="BowireApiEndpoints"/> when the endpoint handlers were
/// split into per-feature files. Everything here is <c>internal</c>
/// because Bowire's REST API surface is an implementation detail of
/// the embedded UI — it's not a public contract.
/// </summary>
internal static class BowireEndpointHelpers
{
    /// <summary>
    /// JSON serialiser settings used for every endpoint response. Camel
    /// case so the JS layer can read fields with their idiomatic names,
    /// nulls dropped to keep the wire small, no indentation because the
    /// browser doesn't render it. <c>UnsafeRelaxedJsonEscaping</c> keeps
    /// quotes and non-ASCII characters literal — the default
    /// <c>JavaScriptEncoder</c> escapes them as <c>"</c> /
    /// <c>ü</c> for HTML/script-injection safety, but Bowire never
    /// embeds responses inside HTML or scripts, only fetches them as
    /// <c>application/json</c>, so the escapes were pure noise that made
    /// the streaming-frame pane harder to read for users with German /
    /// Japanese / Russian payloads.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Magic metadata key prefix the JS apikey-with-location=query helper
    /// uses to mark entries that need to go on the URL instead of the
    /// HTTP headers. Stripped by <see cref="ApplyQueryAuthHints"/>.
    /// </summary>
    internal const string QueryAuthPrefix = "__bowireQuery__";

    /// <summary>
    /// Resolve a hint token to its target plugin id and any side-channel
    /// metadata the plugin should see for this call. The hint mechanism is
    /// primarily a plugin pin (<c>grpc@</c>, <c>rest@</c>, ...) but a few
    /// hints are <em>transport variants</em> of an existing plugin and need
    /// to carry an extra "use transport X" bit into the dispatch — Bowire's
    /// first such variant is <c>grpcweb@</c>, which pins the gRPC plugin
    /// while flipping it to gRPC-Web mode via the
    /// <c>X-Bowire-Grpc-Transport: web</c> header
    /// (<c>BowireGrpcProtocol.TransportMetadataKey</c> in the grpc plugin).
    /// <para>
    /// The mapping table here is intentionally tiny and lives in core (not
    /// in the gRPC plugin) so the discovery + invoke endpoints can apply it
    /// uniformly without taking a hard reference to plugin assemblies — the
    /// metadata key stays a magic string at the dispatch boundary. Future
    /// plugins (Akka classic/cluster, MQTT v3/v5, ...) can extend this
    /// table the same way without changing the
    /// <see cref="BowireServerUrl"/> grammar.
    /// </para>
    /// </summary>
    internal static (string PluginId, KeyValuePair<string, string>? TransportMetadata) ResolveHint(string hint)
    {
        if (string.IsNullOrEmpty(hint))
            return (hint, null);

        // CA1308 prefers ToUpperInvariant for case normalisation, but
        // comparing against a lowercase literal is the natural shape; use
        // OrdinalIgnoreCase equality instead so we don't pay the CA1308 hit
        // and don't double-allocate the input string.
        if (string.Equals(hint, "grpcweb", StringComparison.OrdinalIgnoreCase))
        {
            // grpcweb@ pins the gRPC plugin and asks it to wrap the inner
            // handler with GrpcWebHandler — see the plugin's BuildChannel.
            return ("grpc",
                new KeyValuePair<string, string>(
                    "X-Bowire-Grpc-Transport", "web"));
        }
        return (hint, null);
    }

    private static BowireProtocolRegistry? _registry;

    /// <summary>
    /// Returns the cached protocol registry. Set by
    /// <see cref="BowireApiEndpoints.Map"/> on startup so subsequent
    /// requests don't pay the assembly-scan cost. Falls back to a fresh
    /// discovery if a request comes in before <c>Map</c> ran (defensive,
    /// shouldn't happen in practice).
    /// </summary>
    public static BowireProtocolRegistry GetRegistry() =>
        _registry ?? BowireProtocolRegistry.Discover();

    /// <summary>Set the cached registry. Called once from BowireApiEndpoints.Map.</summary>
    public static void SetRegistry(BowireProtocolRegistry registry) => _registry = registry;

    /// <summary>
    /// Resolve a logger from the request's <see cref="IServiceProvider"/>.
    /// Per-request resolution (not a static field) is required because the
    /// integration tests run multiple <c>WebApplicationBuilder</c> hosts in
    /// the same process — a static logger field would race between fixtures
    /// and pick up an EventLog provider from a different host. The
    /// underlying <see cref="LoggerFactory"/> caches loggers internally so
    /// the per-request cost is just a dictionary lookup.
    /// </summary>
    public static ILogger GetLogger(HttpContext ctx)
    {
        var factory = ctx.RequestServices.GetService<ILoggerFactory>();
        return factory?.CreateLogger("Kuestenlogik.Bowire") ?? NullLogger.Instance;
    }

    /// <summary>
    /// Strip CR/LF from a string before it's passed into a log message.
    /// Stops log-forging — an attacker who controls e.g. a service
    /// name or URL could otherwise smuggle fake log lines by embedding
    /// "\r\n[INFO] forged-line" in the value.
    /// </summary>
    public static string SafeLog(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Replace('\r', '_').Replace('\n', '_');

    /// <summary>
    /// Resolve the target server URL for a request — either the explicit
    /// option set by the host (embedded mode), or the request's own
    /// scheme + host as a fallback.
    /// </summary>
    public static string ResolveServerUrl(BowireOptions options, HttpRequest request)
    {
        if (!string.IsNullOrEmpty(options.ServerUrl))
            return options.ServerUrl;

        var scheme = request.Scheme;
        var host = request.Host;
        return $"{scheme}://{host}";
    }

    /// <summary>
    /// The JS apikey-with-location=query helper marks its entries with a
    /// magic <c>__bowireQuery__</c> prefix so the metadata dict can carry
    /// both real headers and "this needs to go on the URL" hints. This
    /// helper strips those entries from the metadata, appends them as
    /// query parameters to the server URL, and returns both pieces — so
    /// every invoke / stream / channel-open path can apply the same fix
    /// before delegating to the protocol plugin.
    /// </summary>
    internal static (string ServerUrl, Dictionary<string, string>? Metadata) ApplyQueryAuthHints(
        string serverUrl, Dictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return (serverUrl, metadata);

        List<KeyValuePair<string, string>>? queryPairs = null;
        Dictionary<string, string>? sanitized = null;
        foreach (var (k, v) in metadata)
        {
            if (k.StartsWith(QueryAuthPrefix, StringComparison.Ordinal))
            {
                queryPairs ??= [];
                queryPairs.Add(new(k[QueryAuthPrefix.Length..], v));
            }
            else
            {
                sanitized ??= new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
                sanitized[k] = v;
            }
        }

        if (queryPairs is null) return (serverUrl, metadata);

        var separator = serverUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var sb = new System.Text.StringBuilder(serverUrl);
        foreach (var (name, value) in queryPairs)
        {
            sb.Append(separator);
            sb.Append(Uri.EscapeDataString(name));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return (sb.ToString(), sanitized);
    }

    /// <summary>
    /// Merge proto-sourced services with protocol-discovered services.
    /// Proto sources take precedence (they appear first); protocol services
    /// are added for any names not already present.
    /// </summary>
    public static List<BowireServiceInfo> MergeServices(
        List<BowireServiceInfo> protoServices, List<BowireServiceInfo> protocolServices)
    {
        var merged = new List<BowireServiceInfo>(protoServices);
        var knownNames = new HashSet<string>(protoServices.Select(s => s.Name));

        foreach (var svc in protocolServices)
        {
            if (!knownNames.Contains(svc.Name))
                merged.Add(svc);
        }

        return merged;
    }
}

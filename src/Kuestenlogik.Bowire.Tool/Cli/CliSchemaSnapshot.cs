// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Shared resolution of an "API snapshot" for the schema-oriented CLI commands
/// (<c>bowire diff</c>, <c>bowire lint</c>): a source string is either a
/// <c>.json</c> snapshot file (the same service-list shape <c>GET /api/services</c>
/// emits) or a live URL to discover. Kept in one place so both commands read a
/// side the same way and the snapshot format stays interchangeable.
/// </summary>
internal static class CliSchemaSnapshot
{
    // JsonSerializerDefaults.Web = camelCase + case-insensitive, matching the
    // workbench's /api/services envelope so snapshots round-trip either way.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Resolve one snapshot source: a snapshot file if the path exists,
    /// otherwise a live URL to discover. Returns <c>null</c> (after writing a
    /// message to <paramref name="errW"/>) on failure.
    /// </summary>
    /// <remarks>
    /// <c>descriptorSetPath</c> is forwarded to discovery when
    /// <c>source</c> turns out to be a live URL; it is irrelevant when the
    /// source is a snapshot file on disk.
    /// </remarks>
    public static async Task<List<BowireServiceInfo>?> ResolveAsync(
        string source, string? protocolId, TextWriter errW, CancellationToken ct,
        string? descriptorSetPath = null)
    {
        if (File.Exists(source))
        {
            try
            {
                var json = await File.ReadAllTextAsync(source, ct).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<BowireServiceInfo>>(json, Json);
                if (list is null)
                {
                    await errW.WriteLineAsync($"Snapshot '{source}' did not parse into a service list.").ConfigureAwait(false);
                }

                return list;
            }
            catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
            {
                await errW.WriteLineAsync($"Failed to read snapshot '{source}': {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }

        return await DiscoverAsync(source, protocolId, errW, ct, descriptorSetPath).ConfigureAwait(false);
    }

    /// <summary>Discover a live URL into a service list, or <c>null</c> on failure.</summary>
    /// <remarks>
    /// <c>descriptorSetPath</c> is a compiled gRPC descriptor set, for a
    /// server that does not answer reflection. Optional and last so existing
    /// callers keep compiling; a non-gRPC plugin ignores it.
    /// </remarks>
    public static async Task<List<BowireServiceInfo>?> DiscoverAsync(
        string url, string? protocolId, TextWriter errW, CancellationToken ct,
        string? descriptorSetPath = null)
    {
        var id = string.IsNullOrWhiteSpace(protocolId) ? PickProtocolId(url) : protocolId;
        var protocol = ResolveProtocol(id);
        if (protocol is null)
        {
            await errW.WriteLineAsync(
                $"Protocol plugin '{id}' is not loaded. Pass --protocol with an installed plugin id.").ConfigureAwait(false);
            return null;
        }

        // Plugin DiscoverAsync is a 3rd-party transport surface: any failure
        // there is a discovery failure, not a crash. Filtered so cancellation
        // and OOM still propagate (the no-pragma boundary-catch convention).
        try
        {
            return await protocol.DiscoverAsync(
                url, showInternalServices: true, DescriptorSetMetadata(descriptorSetPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await errW.WriteLineAsync($"Discovery failed for {url}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// A descriptor-set path as the metadata bag the gRPC plugin reads.
    /// </summary>
    /// <remarks>
    /// <c>null</c> when no path was given, which is every non-gRPC run and
    /// most gRPC ones.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string>? DescriptorSetMetadata(string? descriptorSetPath)
        => string.IsNullOrWhiteSpace(descriptorSetPath)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BowireMetadataKeys.GrpcDescriptorSet] = descriptorSetPath,
            };

    /// <summary>
    /// Best-effort protocol pick from a URL scheme; <c>--protocol</c> overrides.
    /// Defaults to REST for http(s) since that is the common case — a gRPC or
    /// GraphQL target over http should pass <c>--protocol</c> explicitly.
    /// </summary>
    private static string PickProtocolId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "rest";
        var scheme = uri.Scheme;
        if (Eq(scheme, "ws") || Eq(scheme, "wss")) return "websocket";
        if (Eq(scheme, "mqtt") || Eq(scheme, "mqtts")) return "mqtt";
        if (Eq(scheme, "nats")) return "nats";
        if (Eq(scheme, "kafka")) return "kafka";
        if (Eq(scheme, "amqp") || Eq(scheme, "amqps")) return "amqp";
        return "rest";

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static IBowireProtocol? ResolveProtocol(string id)
    {
        var registry = BowireProtocolRegistry.Discover();
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}

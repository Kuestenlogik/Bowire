// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.AgentHub;

/// <summary>What an agent sends the hub, at start and on every heartbeat (#128).</summary>
public sealed record BowireAgentRegistration(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("callbackUrl")] string CallbackUrl,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("owner")] string? Owner = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("entries")] IReadOnlyList<BowireCatalogueEntry>? Entries = null,
    [property: JsonPropertyName("heartbeatSeconds")] double? HeartbeatSeconds = null);

/// <summary>An agent as the hub knows it.</summary>
public sealed record BowireHubAgent(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("callbackUrl")] string CallbackUrl,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("entries")] IReadOnlyList<BowireCatalogueEntry> Entries,
    [property: JsonPropertyName("registeredAt")] DateTimeOffset RegisteredAt,
    [property: JsonPropertyName("lastSeen")] DateTimeOffset LastSeen,
    [property: JsonPropertyName("live")] bool Live);

/// <summary>
/// The hub's list of agents (#128): kept in memory, keyed on
/// <c>(serviceName, instanceId)</c> so a heartbeat updates rather than
/// duplicates. An agent is <i>live</i> while its last registration is
/// younger than three of its own heartbeat intervals (or the hub's TTL when
/// it did not say); a stale one stays listed, marked, until it has been
/// silent for ten times that, then it is dropped.
/// </summary>
/// <remarks>
/// Memory only, on purpose: agents push, so a restarted hub is whole again
/// after one heartbeat interval, and there is no second copy of the fleet
/// to go stale.
/// </remarks>
public sealed class BowireHubRegistry(TimeProvider? clock = null, TimeSpan? defaultTtl = null)
{
    private sealed record Slot(BowireAgentRegistration Registration, string AgentId, DateTimeOffset RegisteredAt, DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, Slot> _agents = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(90);

    /// <summary>Record a registration or heartbeat; returns the agent's id, stable across heartbeats.</summary>
    public string Register(BowireAgentRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var id = AgentIdFor(registration.ServiceName, registration.InstanceId);
        var now = _clock.GetUtcNow();
        _agents.AddOrUpdate(id,
            _ => new Slot(registration, id, now, now),
            (_, old) => old with { Registration = registration, LastSeen = now });
        return id;
    }

    /// <summary>Forget an agent; false when it was not known.</summary>
    public bool Remove(string agentId) => _agents.TryRemove(agentId, out _);

    /// <summary>Every agent, live ones first, with the long-silent ones dropped.</summary>
    public IReadOnlyList<BowireHubAgent> List()
    {
        var now = _clock.GetUtcNow();
        var result = new List<BowireHubAgent>();
        foreach (var (id, slot) in _agents)
        {
            var ttl = Ttl(slot.Registration);
            var silent = now - slot.LastSeen;
            if (silent > ttl * 10)
            {
                _agents.TryRemove(new KeyValuePair<string, Slot>(id, slot));
                continue;
            }
            var r = slot.Registration;
            result.Add(new BowireHubAgent(
                id, r.ServiceName, r.InstanceId, r.CallbackUrl, r.Version, r.Owner,
                r.Tags ?? [], r.Entries ?? [], slot.RegisteredAt, slot.LastSeen, Live: silent <= ttl));
        }
        return [.. result.OrderByDescending(a => a.Live).ThenBy(a => a.ServiceName, StringComparer.Ordinal).ThenBy(a => a.InstanceId, StringComparer.Ordinal)];
    }

    private TimeSpan Ttl(BowireAgentRegistration r) =>
        r.HeartbeatSeconds is > 0 and var s ? TimeSpan.FromSeconds(s * 3) : _defaultTtl;

    /// <summary>
    /// A URL-safe id derived from the key, so the same service instance
    /// keeps its id across heartbeats and hub restarts.
    /// </summary>
    public static string AgentIdFor(string serviceName, string instanceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceName + "\n" + instanceId));
        var slug = new string([.. serviceName.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]).Trim('-');
        if (slug.Length > 40) slug = slug[..40];
        return (slug.Length > 0 ? slug + "-" : "") + Convert.ToHexStringLower(hash)[..10];
    }
}

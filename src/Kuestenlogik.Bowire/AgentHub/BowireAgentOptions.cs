// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.AgentHub;

/// <summary>
/// Makes an embedded Bowire an <b>agent</b> of a central hub (#128): it
/// registers itself there and keeps the registration fresh, so the hub
/// shows every Bowire in a fleet in one place.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <see cref="HubUrl"/> is set — in code on
/// <see cref="BowireOptions.Agent"/>, or as <c>Bowire:Agent:HubUrl</c>.
/// Every property falls back to <c>Bowire:Agent:&lt;Name&gt;</c> when code
/// leaves it unset.
/// </para>
/// <para>
/// The agent pushes: at start, then every <see cref="HeartbeatInterval"/>,
/// it sends its name, tags, callback URL and catalogue entries. The hub
/// never has to reach the agent to know about it, and a hub restart is
/// healed by the next heartbeat. On shutdown the agent deregisters.
/// </para>
/// </remarks>
public sealed class BowireAgentOptions
{
    /// <summary>The hub's base URL, e.g. <c>https://bowire-hub.internal</c>; the hub answers under <c>/hub</c> there.</summary>
    public string? HubUrl { get; set; }

    /// <summary>The name the hub lists this agent under; defaults to the application name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Distinguishes instances of one service; defaults to the machine name.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The service's version, shown in the hub.</summary>
    public string? Version { get; set; }

    /// <summary>Who owns the service, shown in the hub.</summary>
    public string? Owner { get; set; }

    /// <summary>
    /// Labels for the hub to filter on — <c>env:prod</c>, <c>region:eu</c>.
    /// <c>parallel-executor</c> offers this Bowire as an executor for
    /// distributed parallel runs.
    /// </summary>
    public List<string> Tags { get; } = [];

    /// <summary>
    /// Where the hub (and an operator) reaches this agent's workbench.
    /// Derived from the listening address and the Bowire route prefix when
    /// unset; set it when the agent sits behind a reverse proxy.
    /// </summary>
    public string? CallbackUrl { get; set; }

    /// <summary>The hub's bootstrap token, sent as <c>Authorization: Bearer</c>.</summary>
    public string? Token { get; set; }

    /// <summary>How often the registration is refreshed. Default 30 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fill every property code left unset from <c>Bowire:Agent:*</c>.</summary>
    internal void ApplyConfiguration(IConfiguration? config)
    {
        if (config is null) return;
        var section = config.GetSection("Bowire:Agent");
        HubUrl ??= NonEmpty(section["HubUrl"]);
        ServiceName ??= NonEmpty(section["ServiceName"]);
        InstanceId ??= NonEmpty(section["InstanceId"]);
        Version ??= NonEmpty(section["Version"]);
        Owner ??= NonEmpty(section["Owner"]);
        CallbackUrl ??= NonEmpty(section["CallbackUrl"]);
        Token ??= NonEmpty(section["Token"]);
        if (Tags.Count == 0)
        {
            var tags = section.GetSection("Tags");
            if (!string.IsNullOrWhiteSpace(tags.Value))
                Tags.AddRange(tags.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Tags.AddRange(tags.GetChildren().Select(c => c.Value).OfType<string>().Where(v => v.Length > 0));
        }
        if (NonEmpty(section["HeartbeatSeconds"]) is { } seconds
            && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
        {
            HeartbeatInterval = TimeSpan.FromSeconds(s);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

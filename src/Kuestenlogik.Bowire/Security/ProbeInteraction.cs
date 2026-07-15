// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// One out-of-band callback attributed to a probe — the target reached out to
/// a host the probe planted in it, which is the evidence a *blind* finding
/// rests on (blind SSRF / RCE / XXE leave nothing in the response itself).
/// </summary>
/// <remarks>
/// Deliberately transport-neutral and free of any interaction-server's wire
/// shape: the optional <c>Kuestenlogik.Bowire.Oast</c> package owns the
/// protocol details and maps them onto this type, so Core carries the
/// evaluation axis without depending on the OAST feature or on interactsh.
/// </remarks>
public sealed record ProbeInteraction
{
    /// <summary>Transport the callback arrived on: <c>dns</c>, <c>http</c>, <c>smtp</c>, <c>ldap</c>, …</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "";

    /// <summary>The callback host that was contacted — ties the interaction to the probe that planted it.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Address the callback came from — usually the target itself, and the finding's core evidence.</summary>
    [JsonPropertyName("remoteAddress")]
    public string? RemoteAddress { get; init; }

    /// <summary>The raw callback as the catcher recorded it, quoted into the finding.</summary>
    [JsonPropertyName("rawRequest")]
    public string? RawRequest { get; init; }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Oast;

/// <summary>
/// One out-of-band callback the interaction server observed — the proof that
/// a target reached out to a host we planted in a probe. Field names mirror
/// the interactsh server's interaction JSON verbatim so the payload
/// deserialises without a translation layer.
/// </summary>
public sealed record OastInteraction
{
    /// <summary>Transport the callback arrived on: <c>dns</c>, <c>http</c>, <c>smtp</c>, <c>ldap</c>, …</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "";

    /// <summary>The full callback host that was contacted (correlation id + nonce + domain).</summary>
    [JsonPropertyName("full-id")]
    public string? FullId { get; init; }

    /// <summary>The correlation id slice — ties the callback back to the probe that planted it.</summary>
    [JsonPropertyName("unique-id")]
    public string? UniqueId { get; init; }

    /// <summary>DNS query type (<c>A</c>, <c>AAAA</c>, <c>TXT</c>, …) when <see cref="Protocol"/> is dns.</summary>
    [JsonPropertyName("q-type")]
    public string? QType { get; init; }

    /// <summary>IP the callback came from — usually the target itself, which is the finding's evidence.</summary>
    [JsonPropertyName("remote-address")]
    public string? RemoteAddress { get; init; }

    /// <summary>Server-side timestamp of the interaction.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The raw request as the catcher saw it — quoted into the finding as evidence.</summary>
    [JsonPropertyName("raw-request")]
    public string? RawRequest { get; init; }
}

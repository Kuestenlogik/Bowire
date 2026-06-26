// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// One row in a Bowire URL/service catalogue — a discoverable target
/// surfaced by an <see cref="IBowireCatalogueProvider"/> (#136).
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors the documented JSON wire format so callers can
/// deserialise a remote catalogue document straight into this record
/// without an intermediate DTO:
/// </para>
/// <code>
/// {
///   "url": "https://staging-payments.example.com",
///   "name": "Staging Payments",
///   "protocols": ["rest", "grpc"],
///   "tags": ["env:staging", "team:payments"],
///   "schema": "https://staging-payments.example.com/openapi.json"
/// }
/// </code>
/// <para>
/// Only <see cref="Url"/> is required. Everything else is metadata
/// the workbench can use to filter, pre-pin the right protocol plugin
/// (Url's <c>protocol@</c> hint stays the per-call override), or
/// surface a friendly label in the Sources rail.
/// </para>
/// </remarks>
/// <param name="Url">
/// The discoverable target — anything Bowire's protocol plugins would
/// accept as a server URL. Required. Empty / null entries are dropped
/// by the registry merge.
/// </param>
/// <param name="Name">
/// Optional human-readable label (e.g. <c>"Staging Payments"</c>). When
/// null the workbench falls back to <see cref="Url"/>.
/// </param>
/// <param name="Protocols">
/// Optional list of protocol plugin ids the entry advertises (e.g.
/// <c>["rest", "grpc"]</c>). Used as a discovery hint — the workbench
/// can short-circuit the per-plugin probe fanout for entries that
/// declare their protocols up front.
/// </param>
/// <param name="Tags">
/// Optional metadata tags — free-form strings, conventionally
/// <c>"key:value"</c> (e.g. <c>"env:staging"</c>, <c>"team:payments"</c>).
/// The workbench's filter popup surfaces these.
/// </param>
/// <param name="Schema">
/// Optional URL to an OpenAPI / GraphQL SDL / .proto schema document
/// that pins the entry's wire shape — saves the workbench from probing
/// reflection / introspection at discovery time.
/// </param>
public sealed record BowireCatalogueEntry(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("protocols")] IReadOnlyList<string>? Protocols = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("schema")] string? Schema = null);

/// <summary>
/// Top-level shape of a catalogue document. Matches the spec in #136 —
/// providers that fetch JSON over the wire (local file, http, agent)
/// deserialise into this record. <see cref="Version"/> defaults to 1
/// so payloads that omit the field stay parsable.
/// </summary>
/// <param name="Version">
/// Document schema version. Currently 1. Bumped only on a
/// breaking shape change so old providers can refuse to consume
/// a payload they don't understand.
/// </param>
/// <param name="Entries">The catalogue rows. Empty / null is allowed.</param>
public sealed record BowireCatalogueDocument(
    [property: JsonPropertyName("version")] int Version = 1,
    [property: JsonPropertyName("entries")] IReadOnlyList<BowireCatalogueEntry>? Entries = null);

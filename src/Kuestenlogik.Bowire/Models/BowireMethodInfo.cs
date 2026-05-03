// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// Describes a single method within a service. Originally modeled after gRPC,
/// extended with optional REST/HTTP annotations so the same shape can describe
/// REST endpoints discovered from OpenAPI documents.
/// </summary>
public sealed record BowireMethodInfo(
    string Name,
    string FullName,
    bool ClientStreaming,
    bool ServerStreaming,
    BowireMessageInfo InputType,
    BowireMessageInfo OutputType,
    string MethodType)
{
    /// <summary>
    /// HTTP verb for REST methods (GET, POST, PUT, PATCH, DELETE, ...).
    /// Null for non-REST protocols.
    /// </summary>
    public string? HttpMethod { get; init; }

    /// <summary>
    /// Path template for REST methods, e.g. "/users/{id}".
    /// Null for non-REST protocols.
    /// </summary>
    public string? HttpPath { get; init; }

    /// <summary>One-line summary from the source schema (REST: OpenAPI Operation.Summary). Optional.</summary>
    public string? Summary { get; init; }

    /// <summary>Long-form description, may contain Markdown (REST: OpenAPI Operation.Description). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>True if the source schema marks this operation as deprecated.</summary>
    public bool Deprecated { get; init; }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

#pragma warning disable CA1819 // SchemaDescriptor is a raw FileDescriptorSet —
                              // defensive copies on every property access
                              // would waste memory for what is immutable by
                              // convention.

/// <summary>
/// Describes a gRPC service discovered via reflection or proto file import.
/// </summary>
public sealed record BowireServiceInfo(
    string Name,
    string Package,
    List<BowireMethodInfo> Methods)
{
    /// <summary>
    /// How this service was discovered: "reflection" (default) or "proto" (from .proto file).
    /// When "proto", invocation requires gRPC Server Reflection on the target server.
    /// </summary>
    public string Source { get; set; } = "reflection";

    /// <summary>Human-readable description from the source schema (REST: OpenAPI Info.Description). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>API version string from the source schema (REST: OpenAPI Info.Version). Optional.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// The URL this service was discovered from. Used by multi-URL setups so
    /// invocations can route back to the right base URL. For embedded mode this
    /// is null. Mutable so the discovering plugin can set it after construction
    /// without rebuilding the record.
    /// </summary>
    public string? OriginUrl { get; set; }

    /// <summary>
    /// True when this service was discovered from a user-uploaded schema file
    /// (.proto, OpenAPI .json/.yaml). The Source Selector uses this to keep
    /// "Schema Files" mode separate from "Server URL" mode. Mutable so plugins
    /// can flip it after construction.
    /// </summary>
    public bool IsUploaded { get; set; }

    /// <summary>
    /// gRPC-only: serialised <c>FileDescriptorSet</c> bytes for the protos
    /// that define this service (plus its transitive deps). Captured by the
    /// gRPC plugin at discovery time from Server Reflection so mock-server
    /// replay can expose Server Reflection on the mock itself — which lets a
    /// second Bowire workbench auto-discover the mocked services.
    /// <c>null</c> for protocols that have no proto schema (REST, SignalR,
    /// GraphQL, MQTT, ...).
    /// </summary>
    public byte[]? SchemaDescriptor { get; set; }
}

#pragma warning restore CA1819

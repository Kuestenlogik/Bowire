// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Source for proto file definitions.
/// Used to provide service schemas when gRPC Server Reflection is not available.
/// </summary>
public sealed class ProtoSource
{
    /// <summary>Proto file content as string.</summary>
    public string? Content { get; set; }

    /// <summary>Path to a .proto file on disk.</summary>
    public string? FilePath { get; set; }

    /// <summary>Create from inline proto content.</summary>
    public static ProtoSource FromContent(string protoContent) => new() { Content = protoContent };

    /// <summary>Create from a file path.</summary>
    public static ProtoSource FromFile(string path) => new() { FilePath = path };
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.SchemaDesigner;

/// <summary>
/// What a node in the schema graph stands for.
/// </summary>
public enum SchemaNodeKind
{
    /// <summary>A message / composite type.</summary>
    Message,
    /// <summary>An enum type.</summary>
    Enum,
    /// <summary>A single method on a service.</summary>
    Method,
}

/// <summary>
/// Why two nodes are connected.
/// </summary>
public enum SchemaEdgeKind
{
    /// <summary>A field of the source message is of the target's type.</summary>
    Field,
    /// <summary>The source method takes the target as its request type.</summary>
    Request,
    /// <summary>The source method returns the target as its response type.</summary>
    Response,
}

/// <summary>
/// One type or method in the graph.
/// </summary>
/// <param name="Id">
/// Stable identity. For types the fully-qualified name without the leading
/// dot (<c>harbor.v1.PortCall</c>); for methods <c>package.Service/Method</c>.
/// </param>
/// <param name="Name">The last segment of <paramref name="Id"/> — what the node is labelled with.</param>
/// <param name="Kind">Whether this is a message, an enum or a method.</param>
/// <param name="File">The schema file that defines it, e.g. <c>harbor/v1/port_call.proto</c>.</param>
/// <param name="Service">
/// For <see cref="SchemaNodeKind.Method"/> nodes, the fully-qualified service
/// name the method belongs to. <c>null</c> for types.
/// </param>
public sealed record SchemaNode(
    string Id,
    string Name,
    SchemaNodeKind Kind,
    string File,
    string? Service = null)
{
    /// <summary>
    /// How many fields the message declares. Zero for enums and methods.
    /// </summary>
    public int FieldCount { get; init; }

    /// <summary>
    /// How many edges point AT this node — the "who uses this" count the
    /// usage filter in the rail sorts and thresholds on. Filled in by
    /// <see cref="SchemaGraphBuilder"/> once every edge is known.
    /// </summary>
    public int UsageCount { get; init; }

    /// <summary>
    /// True when the type is declared inside another message
    /// (<c>Outer.Inner</c>), which is worth showing differently: a nested
    /// type with one user is a detail, not a shared concept.
    /// </summary>
    public bool IsNested { get; init; }
}

/// <summary>
/// One relationship between two nodes.
/// </summary>
/// <param name="From">The <see cref="SchemaNode.Id"/> the edge starts at.</param>
/// <param name="To">The <see cref="SchemaNode.Id"/> the edge points at.</param>
/// <param name="Kind">Why the two are connected.</param>
/// <param name="Label">
/// The field name for <see cref="SchemaEdgeKind.Field"/> edges, so a reader
/// can tell two references to the same type apart. Empty for method edges,
/// whose kind already says everything.
/// </param>
public sealed record SchemaEdge(
    string From,
    string To,
    SchemaEdgeKind Kind,
    string Label = "")
{
    /// <summary>True for a <c>repeated</c> field.</summary>
    public bool IsRepeated { get; init; }

    /// <summary>True for a map field, whose value type the edge points at.</summary>
    public bool IsMap { get; init; }
}

/// <summary>
/// The whole type graph of one discovered schema: every message, enum and
/// method, plus every reference between them.
/// </summary>
/// <param name="Nodes">Every node, de-duplicated by <see cref="SchemaNode.Id"/>.</param>
/// <param name="Edges">Every reference. Parallel edges are kept — two fields of the same type are two facts.</param>
/// <param name="Files">Every schema file that contributed a node, sorted, for the per-file filter.</param>
public sealed record SchemaGraph(
    IReadOnlyList<SchemaNode> Nodes,
    IReadOnlyList<SchemaEdge> Edges,
    IReadOnlyList<string> Files)
{
    /// <summary>An empty graph — what a schema with no descriptors yields.</summary>
    public static SchemaGraph Empty { get; } = new([], [], []);
}

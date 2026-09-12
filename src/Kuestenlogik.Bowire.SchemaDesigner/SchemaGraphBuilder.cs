// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace Kuestenlogik.Bowire.SchemaDesigner;

/// <summary>
/// Builds a <see cref="SchemaGraph"/> from serialised
/// <c>FileDescriptorSet</c> bytes — the same descriptor set the gRPC plugin
/// already captures at discovery time and hangs on
/// <c>BowireServiceInfo.SchemaDescriptor</c>.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor set, not the discovered <c>BowireMessageInfo</c> tree, is
/// the source on purpose. That tree inlines each referenced message at the
/// point of use and de-duplicates with a visited-set that spans the whole
/// tree, so the SECOND appearance of a type comes back as an empty stub —
/// exactly the cross-reference this rail exists to show. The descriptor set
/// is flat and states each type once, which is what a graph needs.
/// </para>
/// <para>
/// Nothing here is gRPC-specific beyond the input format: the graph model is
/// protocol-neutral, so a GraphQL SDL or OpenAPI component builder can emit
/// the same <see cref="SchemaGraph"/> later (#247 Phase 2/3) without the rail
/// changing.
/// </para>
/// </remarks>
public static class SchemaGraphBuilder
{
    /// <summary>
    /// Files whose types are plumbing rather than domain. They are referenced
    /// constantly, so including them turns <c>Timestamp</c> into the busiest
    /// node in the graph and buries the structure the operator came to see.
    /// </summary>
    private const string WellKnownPrefix = "google/protobuf/";

    /// <summary>
    /// Parse and merge every descriptor set, then build the graph.
    /// </summary>
    /// <param name="descriptorSets">
    /// Serialised <c>FileDescriptorSet</c> payloads. Several services usually
    /// carry overlapping sets — each ships its own transitive dependencies —
    /// so files are merged by name and a repeat is ignored rather than
    /// duplicated.
    /// </param>
    /// <param name="includeWellKnown">
    /// Include <c>google/protobuf/*</c> types. Off by default; the rail
    /// offers it as a toggle for the rare schema that genuinely wraps them.
    /// </param>
    /// <returns>The graph, or <see cref="SchemaGraph.Empty"/> if nothing parsed.</returns>
    public static SchemaGraph Build(
        IEnumerable<byte[]> descriptorSets,
        bool includeWellKnown = false)
    {
        ArgumentNullException.ThrowIfNull(descriptorSets);

        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        foreach (var bytes in descriptorSets)
        {
            if (bytes is null || bytes.Length == 0) continue;
            foreach (var file in ParseSet(bytes))
            {
                // First definition of a file wins. A later set carrying the
                // same file carries the same content — it is the same proto
                // reached through a different service's dependency closure.
                files.TryAdd(file.Name, file);
            }
        }

        return files.Count == 0 ? SchemaGraph.Empty : BuildFrom(files.Values, includeWellKnown);
    }

    /// <summary>
    /// Parse one descriptor set, yielding nothing if the bytes are not a
    /// descriptor set at all. A malformed payload is the caller's problem to
    /// report, not a reason to fail the whole graph: one unreadable service
    /// should not blank the rail for the others.
    /// </summary>
    private static RepeatedField<FileDescriptorProto> ParseSet(byte[] bytes)
    {
        try
        {
            return FileDescriptorSet.Parser.ParseFrom(bytes).File;
        }
        catch (InvalidProtocolBufferException)
        {
            return [];
        }
    }

    private static SchemaGraph BuildFrom(
        IEnumerable<FileDescriptorProto> files,
        bool includeWellKnown)
    {
        var nodes = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
        var edges = new List<SchemaEdge>();
        // Every message by full name, so a field's TypeName can be resolved
        // back to the message it names — needed to tell a map entry from an
        // ordinary repeated message field.
        var messages = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        var fileNames = new SortedSet<string>(StringComparer.Ordinal);

        var selected = files
            .Where(f => includeWellKnown || !f.Name.StartsWith(WellKnownPrefix, StringComparison.Ordinal))
            .ToList();

        // Pass 1 — every type gets a node, before any edge is drawn, so an
        // edge never has to guess whether its target exists.
        foreach (var file in selected)
        {
            foreach (var message in file.MessageType)
                CollectMessage(message, file, Qualify(file.Package, message.Name), nodes, messages, isNested: false);

            foreach (var enumType in file.EnumType)
                AddEnum(enumType.Name, Qualify(file.Package, enumType.Name), file, nodes, isNested: false);
        }

        // Pass 2 — field references between the types collected above.
        foreach (var (fullName, message) in messages)
        {
            if (!nodes.ContainsKey(fullName)) continue;
            foreach (var field in message.Field)
                AddFieldEdge(fullName, field, messages, nodes, edges);
        }

        // Pass 3 — methods, and the request / response types they name.
        foreach (var file in selected)
        {
            foreach (var service in file.Service)
            {
                var serviceName = Qualify(file.Package, service.Name);
                foreach (var method in service.Method)
                {
                    var id = $"{serviceName}/{method.Name}";
                    nodes[id] = new SchemaNode(id, method.Name, SchemaNodeKind.Method, file.Name, serviceName);
                    AddMethodEdge(id, method.InputType, SchemaEdgeKind.Request, nodes, edges);
                    AddMethodEdge(id, method.OutputType, SchemaEdgeKind.Response, nodes, edges);
                }
            }
        }

        foreach (var node in nodes.Values) fileNames.Add(node.File);

        return new SchemaGraph(WithUsageCounts(nodes, edges), edges, [.. fileNames]);
    }

    /// <summary>
    /// Add a message and, recursively, the types declared inside it. Map
    /// entries are protoc's own synthetic types and are deliberately left
    /// out: an operator reading the graph wants to see <c>map&lt;string,
    /// Berth&gt;</c> as an edge to <c>Berth</c>, not a <c>BerthsEntry</c>
    /// node they never wrote.
    /// </summary>
    private static void CollectMessage(
        DescriptorProto message,
        FileDescriptorProto file,
        string fullName,
        Dictionary<string, SchemaNode> nodes,
        Dictionary<string, DescriptorProto> messages,
        bool isNested)
    {
        messages[fullName] = message;

        if (!IsMapEntry(message))
        {
            nodes[fullName] = new SchemaNode(fullName, ShortName(fullName), SchemaNodeKind.Message, file.Name)
            {
                FieldCount = message.Field.Count,
                IsNested = isNested,
            };
        }

        foreach (var nested in message.NestedType)
            CollectMessage(nested, file, $"{fullName}.{nested.Name}", nodes, messages, isNested: true);

        foreach (var enumType in message.EnumType)
            AddEnum(enumType.Name, $"{fullName}.{enumType.Name}", file, nodes, isNested: true);
    }

    private static void AddEnum(
        string name,
        string fullName,
        FileDescriptorProto file,
        Dictionary<string, SchemaNode> nodes,
        bool isNested)
    {
        nodes[fullName] = new SchemaNode(fullName, name, SchemaNodeKind.Enum, file.Name) { IsNested = isNested };
    }

    /// <summary>
    /// Draw the edge a single field contributes, if it names a type at all.
    /// A map field names protoc's synthetic entry type, so the edge is
    /// re-pointed at the entry's VALUE — the type the operator wrote.
    /// </summary>
    private static void AddFieldEdge(
        string fromId,
        FieldDescriptorProto field,
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, SchemaNode> nodes,
        List<SchemaEdge> edges)
    {
        if (field.Type is not (FieldDescriptorProto.Types.Type.Message or FieldDescriptorProto.Types.Type.Enum))
            return;

        var target = Strip(field.TypeName);
        var isMap = false;

        if (messages.TryGetValue(target, out var targetMessage) && IsMapEntry(targetMessage))
        {
            isMap = true;
            // A map entry is always `key = 1, value = 2`. A scalar value
            // names no type, and then the map contributes no edge at all.
            var value = targetMessage.Field.FirstOrDefault(f => f.Number == 2);
            if (value is null
                || value.Type is not (FieldDescriptorProto.Types.Type.Message or FieldDescriptorProto.Types.Type.Enum))
                return;
            target = Strip(value.TypeName);
        }

        // A reference to a type that was filtered out (a well-known type, or
        // a file the set never carried) draws nothing: a dangling edge reads
        // as a missing node rather than as an absent one.
        if (!nodes.ContainsKey(target)) return;

        edges.Add(new SchemaEdge(fromId, target, SchemaEdgeKind.Field, field.Name)
        {
            IsRepeated = !isMap && field.Label == FieldDescriptorProto.Types.Label.Repeated,
            IsMap = isMap,
        });
    }

    private static void AddMethodEdge(
        string fromId,
        string typeName,
        SchemaEdgeKind kind,
        Dictionary<string, SchemaNode> nodes,
        List<SchemaEdge> edges)
    {
        var target = Strip(typeName);
        if (target.Length == 0 || !nodes.ContainsKey(target)) return;
        edges.Add(new SchemaEdge(fromId, target, kind));
    }

    /// <summary>
    /// Stamp each node with how many edges point at it. This is the number
    /// the "types with N+ usages" filter reads, and the one that separates a
    /// shared concept from a single-use detail.
    /// </summary>
    private static List<SchemaNode> WithUsageCounts(
        Dictionary<string, SchemaNode> nodes,
        List<SchemaEdge> edges)
    {
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
            incoming[edge.To] = incoming.TryGetValue(edge.To, out var n) ? n + 1 : 1;

        var result = new List<SchemaNode>(nodes.Count);
        foreach (var node in nodes.Values)
            result.Add(node with { UsageCount = incoming.TryGetValue(node.Id, out var count) ? count : 0 });

        result.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    private static bool IsMapEntry(DescriptorProto message) =>
        message.Options is { MapEntry: true };

    private static string Qualify(string package, string name) =>
        package.Length == 0 ? name : $"{package}.{name}";

    private static string Strip(string typeName) =>
        typeName.TrimStart('.');

    private static string ShortName(string fullName)
    {
        var cut = fullName.LastIndexOf('.');
        return cut < 0 ? fullName : fullName[(cut + 1)..];
    }
}

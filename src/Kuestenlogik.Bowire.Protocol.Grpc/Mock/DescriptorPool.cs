// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Mock;

/// <summary>
/// Builds a live <see cref="FileDescriptor"/> pool from the base64-encoded
/// <c>schemaDescriptor</c> payloads attached to gRPC recording steps. The
/// pool is consumed by the mock's gRPC Server Reflection endpoint so a
/// second Bowire workbench can auto-discover the mocked services without
/// needing out-of-band access to the original <c>.proto</c> files.
/// </summary>
public static class DescriptorPool
{
    /// <summary>
    /// Parse every unique <c>schemaDescriptor</c> in the recording and return
    /// the flat list of <see cref="FileDescriptor"/> objects they contain.
    /// Steps without a descriptor are silently skipped. Parse failures on an
    /// individual step are surfaced as an <see cref="InvalidDataException"/>
    /// — we don't want Server Reflection to silently lie about what's
    /// actually mocked.
    /// </summary>
    public static IReadOnlyList<FileDescriptor> BuildFrom(BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        // De-duplicate by the base64 string itself — identical services across
        // multiple steps end up with identical descriptor blobs. Hashing the
        // string is much cheaper than parsing + deep-comparing descriptors.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var protos = new List<FileDescriptorProto>();

        foreach (var step in recording.Steps)
        {
            if (string.IsNullOrEmpty(step.SchemaDescriptor)) continue;
            if (!seen.Add(step.SchemaDescriptor)) continue;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(step.SchemaDescriptor);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Step '{step.Id}' has malformed base64 in 'schemaDescriptor': {ex.Message}",
                    ex);
            }

            FileDescriptorSet set;
            try
            {
                set = FileDescriptorSet.Parser.ParseFrom(bytes);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new InvalidDataException(
                    $"Step '{step.Id}' has invalid FileDescriptorSet bytes: {ex.Message}",
                    ex);
            }

            foreach (var proto in set.File)
            {
                // Two services can share a transitive dependency (e.g.
                // google/protobuf/timestamp.proto); de-duplicate by file name
                // so FileDescriptor.BuildFromByteStrings doesn't choke on the
                // second copy.
                if (!protos.Any(p => p.Name == proto.Name))
                    protos.Add(proto);
            }
        }

        if (protos.Count == 0) return [];

        // Topologically sort by dependency so each file is built after its
        // deps. FileDescriptor.BuildFromByteStrings handles ordering itself
        // in newer protobuf builds, but being explicit is defensive.
        var ordered = TopologicalSort(protos);
        var byteStrings = ordered.Select(p => p.ToByteString()).ToList();

        return FileDescriptor.BuildFromByteStrings(byteStrings);
    }

    // Simple topo-sort on the Dependency list (each FileDescriptorProto
    // declares its deps by file name). Cycles aren't expected in well-formed
    // proto schemas; if any slip through, we keep remaining files in their
    // original order to avoid infinite loops.
    private static List<FileDescriptorProto> TopologicalSort(List<FileDescriptorProto> protos)
    {
        var byName = protos.ToDictionary(p => p.Name);
        var visited = new HashSet<string>();
        var result = new List<FileDescriptorProto>();

        void Visit(FileDescriptorProto proto)
        {
            if (!visited.Add(proto.Name)) return;
            foreach (var depName in proto.Dependency)
            {
                if (byName.TryGetValue(depName, out var dep)) Visit(dep);
            }
            result.Add(proto);
        }

        foreach (var p in protos) Visit(p);
        return result;
    }
}

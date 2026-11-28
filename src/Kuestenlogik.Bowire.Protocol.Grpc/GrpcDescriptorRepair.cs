// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// #1068 — brings file descriptors from a reflection service into the shape
/// <c>protoc</c> writes, before they are built.
/// </summary>
/// <remarks>
/// <para>
/// Node's <c>@grpc/reflection</c> (over protobufjs) serves descriptors that
/// <c>FileDescriptor.BuildFromByteStrings</c> refuses: field type names
/// relative to their scope, no <c>dependency</c> list, a well-known type in a
/// file of its own under a nonstandard name (<c>google_protobuf.proto</c>),
/// and proto3 <c>optional</c> fields with their synthetic oneof but without
/// <c>proto3_optional</c>. Every call to such a server failed with an empty
/// service list.
/// </para>
/// <para>
/// The repair is a no-op for what <c>protoc</c> writes: every name already
/// qualified, every dependency declared, the well-known types under their
/// own names.
/// </para>
/// </remarks>
internal static class GrpcDescriptorRepair
{
    // Every well-known type's symbols, by the file protoc names it in.
    private static readonly Lazy<Dictionary<string, string>> s_wellKnown = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[]
        {
            AnyReflection.Descriptor, ApiReflection.Descriptor, DurationReflection.Descriptor,
            EmptyReflection.Descriptor, FieldMaskReflection.Descriptor, SourceContextReflection.Descriptor,
            StructReflection.Descriptor, TimestampReflection.Descriptor, TypeReflection.Descriptor,
            WrappersReflection.Descriptor, DescriptorReflection.Descriptor,
        })
        {
            foreach (var m in file.MessageTypes) AddMessage(map, m, file.Name);
            foreach (var e in file.EnumTypes) map[e.FullName] = file.Name;
        }
        return map;

        static void AddMessage(Dictionary<string, string> map, MessageDescriptor m, string file)
        {
            map[m.FullName] = file;
            foreach (var nested in m.NestedTypes) AddMessage(map, nested, file);
            foreach (var e in m.EnumTypes) map[e.FullName] = file;
        }
    });

    private static readonly HashSet<string> s_wellKnownFiles =
        new(s_wellKnown.Value.Values, StringComparer.Ordinal);

    public static List<FileDescriptorProto> Repair(IReadOnlyList<FileDescriptorProto> protos)
    {
        var wellKnown = s_wellKnown.Value;

        // A file that only restates well-known types under another name is
        // dropped: the canonical file is seeded, and two files defining
        // google.protobuf.Empty cannot be built together.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<FileDescriptorProto>();
        foreach (var proto in protos)
        {
            if (proto.Package == "google.protobuf" && !s_wellKnownFiles.Contains(proto.Name))
            {
                var symbols = Symbols(proto).ToList();
                if (symbols.Count > 0 && symbols.All(wellKnown.ContainsKey))
                {
                    renamed[proto.Name] = wellKnown[symbols[0]];
                    continue;
                }
            }
            files.Add(proto.Clone());
        }

        var symbolFile = new Dictionary<string, string>(wellKnown, StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var symbol in Symbols(file)) symbolFile[symbol] = file.Name;
        }

        foreach (var file in files)
        {
            var uses = new HashSet<string>(StringComparer.Ordinal);
            var scope = file.Package;
            foreach (var message in file.MessageType) RepairMessage(message, Join(scope, message.Name), symbolFile, uses);
            foreach (var service in file.Service)
            {
                foreach (var method in service.Method)
                {
                    method.InputType = Qualify(method.InputType, scope, symbolFile, uses);
                    method.OutputType = Qualify(method.OutputType, scope, symbolFile, uses);
                }
            }

            // Declared dependencies on a dropped file point at its canonical
            // name; the files the types come from are declared too.
            var deps = file.Dependency.Select(d => renamed.GetValueOrDefault(d, d)).ToList();
            foreach (var used in uses.Where(u => u != file.Name && !deps.Contains(u))) deps.Add(used);
            file.Dependency.Clear();
            file.Dependency.AddRange(deps.Distinct(StringComparer.Ordinal));
        }
        return files;
    }

    private static void RepairMessage(DescriptorProto message, string scope, Dictionary<string, string> symbolFile, HashSet<string> uses)
    {
        foreach (var field in message.Field)
        {
            if (!string.IsNullOrEmpty(field.TypeName))
                field.TypeName = Qualify(field.TypeName, scope, symbolFile, uses);
            if (!string.IsNullOrEmpty(field.Extendee))
                field.Extendee = Qualify(field.Extendee, scope, symbolFile, uses);

            // A synthetic oneof (_name) holding only this field is how proto3
            // optional looks on the wire; protoc marks the field as such.
            if (field.HasOneofIndex && !field.Proto3Optional
                && field.OneofIndex < message.OneofDecl.Count
                && message.OneofDecl[field.OneofIndex].Name == "_" + field.Name
                && message.Field.Count(f => f.HasOneofIndex && f.OneofIndex == field.OneofIndex) == 1)
            {
                field.Proto3Optional = true;
            }
        }
        foreach (var nested in message.NestedType) RepairMessage(nested, Join(scope, nested.Name), symbolFile, uses);
    }

    // protobuf's scoping: a relative name is looked up from the innermost
    // scope outwards; the first match wins.
    private static string Qualify(string name, string scope, Dictionary<string, string> symbolFile, HashSet<string> uses)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.StartsWith('.'))
        {
            if (symbolFile.TryGetValue(name[1..], out var known)) uses.Add(known);
            return name;
        }
        var parts = string.IsNullOrEmpty(scope) ? [] : scope.Split('.');
        for (var depth = parts.Length; depth >= 0; depth--)
        {
            var candidate = Join(string.Join('.', parts.Take(depth)), name);
            if (symbolFile.TryGetValue(candidate, out var file))
            {
                uses.Add(file);
                return "." + candidate;
            }
        }
        return name;
    }

    private static IEnumerable<string> Symbols(FileDescriptorProto file)
    {
        foreach (var m in file.MessageType)
            foreach (var s in Symbols(m, Join(file.Package, m.Name))) yield return s;
        foreach (var e in file.EnumType) yield return Join(file.Package, e.Name);
    }

    private static IEnumerable<string> Symbols(DescriptorProto message, string fullName)
    {
        yield return fullName;
        foreach (var nested in message.NestedType)
            foreach (var s in Symbols(nested, Join(fullName, nested.Name))) yield return s;
        foreach (var e in message.EnumType) yield return Join(fullName, e.Name);
    }

    private static string Join(string scope, string name) => string.IsNullOrEmpty(scope) ? name : scope + "." + name;
}

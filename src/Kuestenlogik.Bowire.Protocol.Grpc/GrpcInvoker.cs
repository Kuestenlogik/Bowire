// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Executes dynamic gRPC calls using protobuf reflection.
/// Uses raw byte marshalling to avoid FileDescriptor building issues.
/// </summary>
internal sealed class GrpcInvoker : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly GrpcReflectionClient _reflectionClient;
    private readonly MtlsHandlerOwner? _mtlsOwner;

    public GrpcInvoker(
        string serverUrl,
        GrpcReflectionClient reflectionClient,
        MtlsConfig? mtlsConfig = null,
        IConfiguration? configuration = null,
        GrpcTransportMode transportMode = GrpcTransportMode.Native)
    {
        // When a client cert is supplied via the mTLS auth helper, build a
        // SocketsHttpHandler with the cert attached to SslOptions and route
        // gRPC traffic through it. The handler-owner holds the X509 resources
        // alongside the handler so disposal is centralised. mTLS path stays
        // strict — the user already specified a CA / client cert pair.
        SocketsHttpHandler httpHandler;
        if (mtlsConfig is not null)
        {
            _mtlsOwner = MtlsHandlerOwner.CreateSocketsHttpHandler(mtlsConfig, out var mtlsError);
            if (_mtlsOwner is null)
            {
                throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
            }
            httpHandler = (SocketsHttpHandler)_mtlsOwner.Handler;
        }
        else
        {
            // Non-mTLS path picks up Bowire:TrustLocalhostCert via the shared
            // factory, same as the HttpClient-based plugins.
            httpHandler = BowireHttpClientFactory.CreateSocketsHttpHandler(
                configuration, "grpc", serverUrl);
        }

        // Route channel construction through GrpcChannelBuilder so gRPC-Web
        // mode wraps the inner handler with GrpcWebHandler — both with and
        // without the mTLS SocketsHttpHandler inner.
        _channel = GrpcChannelBuilder.BuildChannel(serverUrl, httpHandler, transportMode);
        _reflectionClient = reflectionClient;
    }

    public async Task<InvokeResult> InvokeUnaryAsync(
        string serviceName, string methodName, List<string> jsonMessages,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var resolved = await ResolveMethodAsync(serviceName, methodName, ct);
        var callOptions = BuildCallOptions(metadata, ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var method = CreateRawMethod(resolved.FullMethodName,
                (resolved.ClientStreaming, resolved.ServerStreaming) switch
                {
                    (false, false) => MethodType.Unary,
                    (false, true) => MethodType.ServerStreaming,
                    (true, false) => MethodType.ClientStreaming,
                    _ => MethodType.DuplexStreaming
                });

            if (!resolved.ClientStreaming && !resolved.ServerStreaming)
            {
                var requestBytes = JsonToProtobuf(jsonMessages.FirstOrDefault() ?? "{}", resolved.InputType);
                var responseBytes = await _channel.CreateCallInvoker()
                    .AsyncUnaryCall(method, null, callOptions, requestBytes);
                sw.Stop();

                // Also flow the raw wire bytes through InvokeResult so the
                // mock-server replay path can re-emit them verbatim without
                // runtime protobuf re-encoding (which Google.Protobuf can't
                // do dynamically from a descriptor alone — no DynamicMessage
                // equivalent in C#).
                return new InvokeResult(
                    Response: FormatResponse(responseBytes, resolved.OutputType),
                    DurationMs: sw.ElapsedMilliseconds,
                    Status: "OK",
                    Metadata: [],
                    ResponseBinary: responseBytes);
            }

            if (resolved.ClientStreaming && !resolved.ServerStreaming)
            {
                using var call = _channel.CreateCallInvoker()
                    .AsyncClientStreamingCall(method, null, callOptions);

                foreach (var json in jsonMessages)
                    await call.RequestStream.WriteAsync(JsonToProtobuf(json, resolved.InputType));

                await call.RequestStream.CompleteAsync();
                var responseBytes = await call.ResponseAsync;
                sw.Stop();

                return new InvokeResult(
                    Response: FormatResponse(responseBytes, resolved.OutputType),
                    DurationMs: sw.ElapsedMilliseconds,
                    Status: "OK",
                    Metadata: [],
                    ResponseBinary: responseBytes);
            }

            return new InvokeResult(null, 0,
                "Use the streaming endpoint for server-streaming and duplex calls.", []);
        }
        catch (RpcException ex)
        {
            sw.Stop();
            // Tag keys with '_trailer:' so downstream consumers (and the
            // Phase-3 mock's stateful matcher) can tell trailers apart from
            // regular response headers — gRPC status-code tracking lives in
            // trailers and we don't want to mistake it for a user metadata
            // entry on replay. Schema stays backward-compatible: consumers
            // that don't care still see the values in the same Metadata dict.
            return new InvokeResult(
                Response: ex.Status.Detail,
                DurationMs: sw.ElapsedMilliseconds,
                Status: ex.StatusCode.ToString(),
                Metadata: ex.Trailers.ToDictionary(e => "_trailer:" + e.Key, e => e.Value));
        }
    }

    /// <summary>
    /// Binary-aware server / duplex streaming invocation. Yields each
    /// received frame as a <see cref="StreamFrame"/> carrying both the
    /// JSON rendering (display + Bowire UI) and the raw protobuf wire
    /// bytes (mock-server replay).
    /// </summary>
    public async IAsyncEnumerable<StreamFrame> InvokeStreamingWithFramesAsync(
        string serviceName, string methodName, List<string> jsonMessages,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolved = await ResolveMethodAsync(serviceName, methodName, ct);
        var callOptions = BuildCallOptions(metadata, ct);

        var method = CreateRawMethod(resolved.FullMethodName,
            resolved.ClientStreaming ? MethodType.DuplexStreaming : MethodType.ServerStreaming);

        if (!resolved.ClientStreaming && resolved.ServerStreaming)
        {
            var requestBytes = JsonToProtobuf(jsonMessages.FirstOrDefault() ?? "{}", resolved.InputType);
            using var call = _channel.CreateCallInvoker()
                .AsyncServerStreamingCall(method, null, callOptions, requestBytes);

            await foreach (var responseBytes in call.ResponseStream.ReadAllAsync(ct))
                yield return new StreamFrame(
                    Json: FormatResponse(responseBytes, resolved.OutputType),
                    Binary: responseBytes);
        }
        else if (resolved.ClientStreaming && resolved.ServerStreaming)
        {
            using var call = _channel.CreateCallInvoker()
                .AsyncDuplexStreamingCall(method, null, callOptions);

            var sendTask = Task.Run(async () =>
            {
                foreach (var json in jsonMessages)
                    await call.RequestStream.WriteAsync(JsonToProtobuf(json, resolved.InputType));
                await call.RequestStream.CompleteAsync();
            }, ct);

            await foreach (var responseBytes in call.ResponseStream.ReadAllAsync(ct))
                yield return new StreamFrame(
                    Json: FormatResponse(responseBytes, resolved.OutputType),
                    Binary: responseBytes);

            await sendTask;
        }
    }

    private async Task<ResolvedMethod> ResolveMethodAsync(
        string serviceName, string methodName, CancellationToken ct)
    {
        // Get all file descriptor protos from reflection
        var fileDescProtos = await _reflectionClient.ResolveAllDescriptorsAsync(serviceName, ct);

        if (fileDescProtos.Count == 0)
            throw new InvalidOperationException(
                $"gRPC Reflection returned no file descriptors for '{serviceName}'.");

        // Build FileDescriptors with proper dependency resolution
        var fileDescriptors = BuildFileDescriptors(fileDescProtos);

        if (fileDescriptors.Count == 0)
            throw new InvalidOperationException(
                $"Failed to build FileDescriptors. Proto count: {fileDescProtos.Count}, " +
                $"names: [{string.Join(", ", fileDescProtos.Select(p => p.Name))}]");

        // Find service
        ServiceDescriptor? svcDesc = null;
        foreach (var fd in fileDescriptors)
        {
            svcDesc = fd.Services.FirstOrDefault(s => s.FullName == serviceName);
            if (svcDesc is not null) break;
        }

        if (svcDesc is null)
        {
            var allServices = fileDescriptors
                .SelectMany(fd => fd.Services)
                .Select(s => s.FullName);
            throw new InvalidOperationException(
                $"Service '{serviceName}' not found. Available: [{string.Join(", ", allServices)}]");
        }

        // Find method
        var methodDesc = svcDesc.Methods.FirstOrDefault(m => m.Name == methodName)
            ?? throw new InvalidOperationException(
                $"Method '{methodName}' not found. Available: [{string.Join(", ", svcDesc.Methods.Select(m => m.Name))}]");

        var inputType = methodDesc.InputType
            ?? throw new InvalidOperationException(
                $"InputType is null for {serviceName}/{methodName}");
        var outputType = methodDesc.OutputType
            ?? throw new InvalidOperationException(
                $"OutputType is null for {serviceName}/{methodName}");

        return new ResolvedMethod(
            $"/{serviceName}/{methodName}",
            inputType, outputType,
            methodDesc.IsClientStreaming, methodDesc.IsServerStreaming);
    }

    private static List<FileDescriptor> BuildFileDescriptors(
        List<FileDescriptorProto> protos)
    {
        // Fast path: try building everything in one shot. Google.Protobuf
        // resolves cross-references between bytes in the same call, so when
        // reflection delivered all transitive dependencies (the common case)
        // we can skip the per-file dance entirely. We prepend the seeded
        // well-known types' serialized data so non-trivial deps like
        // google/api/annotations.proto are also available.
        try
        {
            var allBytes = new List<ByteString>();
            foreach (var seed in WellKnownDescriptorBytes())
                allBytes.Add(seed);
            // Topological sort guarantees deps come before dependents in the
            // byte list, which gives BuildFromByteStrings the best chance.
            var sortedForBatch = TopologicalSort(protos);
            foreach (var proto in sortedForBatch)
                allBytes.Add(ByteString.CopyFrom(proto.ToByteArray()));

            var batch = FileDescriptor.BuildFromByteStrings(allBytes);
            // Filter to only the files that were in the input — the seeds are
            // implementation detail and shouldn't show up as discoverable services.
            var inputNames = new HashSet<string>(protos.Select(p => p.Name), StringComparer.Ordinal);
            var filtered = batch.Where(fd => inputNames.Contains(fd.Name)).ToList();
            if (filtered.Count == protos.Count)
                return filtered;
            // Fall through to the per-file builder if the batch was incomplete.
        }
        catch
        {
            // Batch build failed — fall through to the per-file builder
        }

        // Slow path: build descriptors one at a time in dependency order.
        // For each proto, resolve its dependencies from already-built descriptors
        // or from well-known types in the protobuf runtime.
        var sorted = TopologicalSort(protos);
        var built = new Dictionary<string, FileDescriptor>();
        var result = new List<FileDescriptor>();

        // Seed with well-known types (google/protobuf/*.proto)
        SeedWellKnownTypes(built);

        foreach (var proto in sorted)
        {
            if (built.ContainsKey(proto.Name))
                continue;

            // Resolve dependencies
            var deps = new List<FileDescriptor>();
            var missingDeps = false;
            foreach (var depName in proto.Dependency)
            {
                if (built.TryGetValue(depName, out var dep))
                    deps.Add(dep);
                else
                    missingDeps = true;
            }

            // When deps can't be resolved (e.g. google/api/annotations.proto for
            // transcoded gRPC services), strip the import list + custom options
            // and try to build a "schema-only" descriptor. This loses any custom
            // option annotations at the runtime descriptor level, but Bowire only
            // needs the message/service/method shape for unary invocation — the
            // HTTP rules live separately on BowireMethodInfo.HttpMethod/HttpPath.
            if (missingDeps)
            {
                try
                {
                    var schemaOnly = proto.Clone();
                    schemaOnly.Dependency.Clear();
                    schemaOnly.Options = null;
                    foreach (var svc in schemaOnly.Service)
                    {
                        svc.Options = null;
                        foreach (var m in svc.Method) m.Options = null;
                    }
                    var fdStripped = FileDescriptor.BuildFromByteStrings(
                        [ByteString.CopyFrom(schemaOnly.ToByteArray())]);
                    if (fdStripped.Count > 0)
                    {
                        var stripped = fdStripped[^1];
                        built[proto.Name] = stripped;
                        result.Add(stripped);
                    }
                }
                catch
                {
                    // Even the stripped form failed — give up on this proto
                }
                continue;
            }

            try
            {
                // Build this single FileDescriptor with its resolved dependencies
                var fd = FileDescriptor.BuildFromByteStrings(
                    [ByteString.CopyFrom(proto.ToByteArray())]);

                // If there are dependencies, we need to rebuild with them
                if (deps.Count > 0)
                {
                    // Strip the dependencies from the proto, build standalone,
                    // then look up types. This is a workaround for the API not
                    // accepting FileDescriptor[] dependencies.
                    var clone = proto.Clone();
                    clone.Dependency.Clear();

                    try
                    {
                        fd = FileDescriptor.BuildFromByteStrings(
                            [ByteString.CopyFrom(clone.ToByteArray())]);
                    }
                    catch
                    {
                        // If stripping deps fails, try with all deps as byte strings
                        var allBytes = deps
                            .Select(d => ByteString.CopyFrom(d.SerializedData.ToByteArray()))
                            .Append(ByteString.CopyFrom(proto.ToByteArray()))
                            .ToList();
                        try
                        {
                            fd = FileDescriptor.BuildFromByteStrings(allBytes);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                if (fd.Count > 0)
                {
                    // Take the last descriptor (our proto, deps come first)
                    var ourFd = fd[^1];
                    built[proto.Name] = ourFd;
                    result.Add(ourFd);
                }
            }
            catch
            {
                // Skip this descriptor if building fails
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the SerializedData for every file descriptor that gRPC reflection
    /// is unlikely to ship in its FileDescriptorResponse but that user protos
    /// can transitively depend on (well-known types + Google.Api annotations).
    /// Used to prepend these bytes to a single BuildFromByteStrings batch so
    /// the runtime can resolve cross-references.
    /// </summary>
    private static IEnumerable<ByteString> WellKnownDescriptorBytes()
    {
        FileDescriptor[] all =
        [
            Google.Protobuf.WellKnownTypes.Any.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Duration.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Empty.Descriptor.File,
            Google.Protobuf.WellKnownTypes.FieldMask.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Struct.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Timestamp.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Value.Descriptor.File,
            Google.Api.AnnotationsReflection.Descriptor,
            Google.Api.HttpReflection.Descriptor,
        ];
        foreach (var fd in all)
        {
            // SerializedData is the proto bytes the descriptor was built from
            yield return fd.SerializedData;
        }
    }

    private static void SeedWellKnownTypes(Dictionary<string, FileDescriptor> built)
    {
        FileDescriptor[] wellKnown =
        [
            Google.Protobuf.WellKnownTypes.Any.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Duration.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Empty.Descriptor.File,
            Google.Protobuf.WellKnownTypes.FieldMask.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Struct.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Timestamp.Descriptor.File,
            Google.Protobuf.WellKnownTypes.Value.Descriptor.File,
            // google.api.* — used by HTTP transcoding annotations
            // (google/api/annotations.proto, google/api/http.proto)
            Google.Api.AnnotationsReflection.Descriptor,
            Google.Api.HttpReflection.Descriptor,
        ];

        foreach (var fd in wellKnown)
            built.TryAdd(fd.Name, fd);
    }

    private static List<FileDescriptorProto> TopologicalSort(List<FileDescriptorProto> protos)
    {
        var byName = protos.ToDictionary(p => p.Name);
        var visited = new HashSet<string>();
        var result = new List<FileDescriptorProto>();

        foreach (var proto in protos)
            Visit(proto, byName, visited, result);

        return result;
    }

    private static void Visit(
        FileDescriptorProto proto,
        Dictionary<string, FileDescriptorProto> byName,
        HashSet<string> visited,
        List<FileDescriptorProto> result)
    {
        if (!visited.Add(proto.Name)) return;

        foreach (var dep in proto.Dependency)
        {
            if (byName.TryGetValue(dep, out var depProto))
                Visit(depProto, byName, visited, result);
        }

        result.Add(proto);
    }

    /// <summary>
    /// Encode JSON to protobuf wire format using field descriptors.
    /// Works without generated C# classes.
    /// </summary>
    private static byte[] JsonToProtobuf(string json, MessageDescriptor descriptor)
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms);
        WriteMessage(cos, doc.RootElement, descriptor);
        cos.Flush();
        return ms.ToArray();
    }

    private static void WriteMessage(CodedOutputStream cos, JsonElement element, MessageDescriptor descriptor)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            // Find field by JSON name (camelCase) or proto name (snake_case)
            var field = descriptor.FindFieldByName(prop.Name)
                ?? descriptor.Fields.InFieldNumberOrder()
                    .FirstOrDefault(f => string.Equals(f.JsonName, prop.Name, StringComparison.OrdinalIgnoreCase));

            if (field is null) continue;

            if (field.IsRepeated && prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                    WriteField(cos, field, item);
            }
            else
            {
                WriteField(cos, field, prop.Value);
            }
        }
    }

    private static void WriteField(CodedOutputStream cos, FieldDescriptor field, JsonElement value)
    {
        var tag = WireFormat.MakeTag(field.FieldNumber, GetWireType(field));

        switch (field.FieldType)
        {
            case FieldType.String:
                cos.WriteTag(tag);
                cos.WriteString(value.GetString() ?? "");
                break;
            case FieldType.Int32 or FieldType.SInt32:
                cos.WriteTag(tag);
                cos.WriteInt32(value.TryGetInt32(out var i32) ? i32 : 0);
                break;
            case FieldType.Int64 or FieldType.SInt64:
                cos.WriteTag(tag);
                cos.WriteInt64(value.TryGetInt64(out var i64) ? i64 : 0);
                break;
            case FieldType.UInt32:
                cos.WriteTag(tag);
                cos.WriteUInt32(value.TryGetUInt32(out var u32) ? u32 : 0);
                break;
            case FieldType.UInt64:
                cos.WriteTag(tag);
                cos.WriteUInt64(value.TryGetUInt64(out var u64) ? u64 : 0);
                break;
            case FieldType.Double:
                cos.WriteTag(tag);
                cos.WriteDouble(value.TryGetDouble(out var d) ? d : 0);
                break;
            case FieldType.Float:
                cos.WriteTag(tag);
                cos.WriteFloat(value.TryGetSingle(out var f) ? f : 0);
                break;
            case FieldType.Bool:
                cos.WriteTag(tag);
                cos.WriteBool(value.ValueKind == JsonValueKind.True);
                break;
            case FieldType.Bytes:
                cos.WriteTag(tag);
                cos.WriteBytes(ByteString.CopyFrom(Convert.FromBase64String(value.GetString() ?? "")));
                break;
            case FieldType.Enum:
                cos.WriteTag(tag);
                if (value.ValueKind == JsonValueKind.Number)
                    cos.WriteEnum(value.GetInt32());
                else
                {
                    var enumVal = field.EnumType.FindValueByName(value.GetString() ?? "");
                    cos.WriteEnum(enumVal?.Number ?? 0);
                }
                break;
            case FieldType.Message:
                if (field.MessageType is not null && value.ValueKind == JsonValueKind.Object)
                {
                    var subBytes = JsonToProtobuf(value.GetRawText(), field.MessageType);
                    cos.WriteTag(tag);
                    cos.WriteBytes(ByteString.CopyFrom(subBytes));
                }
                break;
        }
    }

    private static WireFormat.WireType GetWireType(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes or FieldType.Message => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint
    };

    /// <summary>
    /// Decode protobuf wire format to JSON using field descriptors.
    /// </summary>
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static string ProtobufToJson(byte[] data, MessageDescriptor descriptor)
    {
        using var cis = new CodedInputStream(data);
        var result = ReadMessage(cis, descriptor);
        return System.Text.Json.JsonSerializer.Serialize(result, IndentedJsonOptions);
    }

    private static Dictionary<string, object?> ReadMessage(CodedInputStream cis, MessageDescriptor descriptor)
    {
        var result = new Dictionary<string, object?>();
        var repeatedFields = new Dictionary<string, List<object?>>();

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            var field = descriptor.FindFieldByNumber(fieldNumber);

            if (field is null)
            {
                cis.SkipLastField();
                continue;
            }

            var value = ReadFieldValue(cis, field, wireType);
            var jsonName = field.JsonName;

            if (field.IsRepeated)
            {
                if (!repeatedFields.TryGetValue(jsonName, out var list))
                {
                    list = [];
                    repeatedFields[jsonName] = list;
                }
                list.Add(value);
            }
            else
            {
                result[jsonName] = value;
            }
        }

        foreach (var (key, list) in repeatedFields)
            result[key] = list;

        return result;
    }

    private static object? ReadFieldValue(CodedInputStream cis, FieldDescriptor field, WireFormat.WireType wireType)
    {
        return field.FieldType switch
        {
            FieldType.String => cis.ReadString(),
            FieldType.Int32 or FieldType.SInt32 => cis.ReadInt32(),
            FieldType.Int64 or FieldType.SInt64 => cis.ReadInt64(),
            FieldType.UInt32 => cis.ReadUInt32(),
            FieldType.UInt64 => cis.ReadUInt64(),
            FieldType.Double => cis.ReadDouble(),
            FieldType.Float => cis.ReadFloat(),
            FieldType.Bool => cis.ReadBool(),
            FieldType.Enum => ResolveEnumName(cis, field),
            FieldType.Bytes => Convert.ToBase64String(cis.ReadBytes().ToByteArray()),
            FieldType.Message when field.MessageType is not null =>
                ReadSubMessage(cis, field.MessageType),
            _ => SkipAndReturnNull(cis)
        };
    }

    private static string ResolveEnumName(CodedInputStream cis, FieldDescriptor field)
    {
        var num = cis.ReadEnum();
        return field.EnumType.FindValueByNumber(num)?.Name ?? num.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object?> ReadSubMessage(CodedInputStream cis, MessageDescriptor descriptor)
    {
        var bytes = cis.ReadBytes().ToByteArray();
        using var subStream = new CodedInputStream(bytes);
        return ReadMessage(subStream, descriptor);
    }

    private static object? SkipAndReturnNull(CodedInputStream cis) { cis.SkipLastField(); return null; }

    private static string FormatResponse(byte[] data, MessageDescriptor descriptor)
        => ProtobufToJson(data, descriptor);

    /// <summary>
    /// Public accessor for FormatResponse, used by GrpcBowireChannel.
    /// </summary>
    internal static string FormatResponsePublic(byte[] data, MessageDescriptor descriptor)
        => ProtobufToJson(data, descriptor);

    /// <summary>
    /// Public accessor for JsonToProtobuf, used by GrpcBowireChannel.
    /// </summary>
    internal static byte[] JsonToProtobufPublic(string json, MessageDescriptor descriptor)
        => JsonToProtobuf(json, descriptor);

    /// <summary>
    /// Public accessor for BuildFileDescriptors, used by GrpcBowireChannel.
    /// </summary>
    internal static List<FileDescriptor> BuildFileDescriptorsPublic(
        List<FileDescriptorProto> protos)
        => BuildFileDescriptors(protos);

    private static Method<byte[], byte[]> CreateRawMethod(
        string fullName, MethodType type)
    {
        var parts = fullName.TrimStart('/').Split('/');
        return new Method<byte[], byte[]>(
            type, parts[0], parts[1],
            Marshallers.Create(
                serializer: static data => data,
                deserializer: static data => data),
            Marshallers.Create(
                serializer: static data => data,
                deserializer: static data => data));
    }

    private static CallOptions BuildCallOptions(
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var headers = new Metadata();
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
                headers.Add(key, value);
        }
        return new CallOptions(headers: headers, cancellationToken: ct);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _mtlsOwner?.Dispose();
    }

    private sealed record ResolvedMethod(
        string FullMethodName,
        MessageDescriptor InputType,
        MessageDescriptor OutputType,
        bool ClientStreaming,
        bool ServerStreaming);
}

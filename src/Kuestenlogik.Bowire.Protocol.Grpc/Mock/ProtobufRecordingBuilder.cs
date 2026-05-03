// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Mock;

/// <summary>
/// Turns a protobuf <see cref="FileDescriptorSet"/> binary (the output
/// of <c>protoc --descriptor_set_out=...</c>) into a synthetic
/// <see cref="BowireRecording"/>: every gRPC method in every
/// service becomes a step with <see cref="BowireRecordingStep.ResponseBinary"/>
/// populated from <see cref="ProtobufSampleEncoder"/>. The existing
/// mock pipeline — <c>UnaryReplayer.ReplayGrpcAsync</c> +
/// <c>DescriptorPool</c> + <c>Grpc.Reflection.ReflectionServiceImpl</c>
/// — handles the rest with no new wiring.
/// </summary>
public static class ProtobufRecordingBuilder
{
    /// <summary>Load a <c>.pb</c> file and build the synthetic recording.</summary>
    public static async Task<BowireRecording> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"gRPC schema file not found: {path}", path);

        var bytes = await File.ReadAllBytesAsync(path, ct);

        FileDescriptorSet fdSet;
        try
        {
            fdSet = FileDescriptorSet.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException(
                $"gRPC schema file '{path}' is not a valid FileDescriptorSet: {ex.Message}. " +
                $"Generate one with: protoc --descriptor_set_out=<path.pb> --include_imports your.proto",
                ex);
        }

        // FileDescriptor.BuildFromByteStrings needs transitive
        // dependencies in the same batch; --include_imports on
        // protoc guarantees that.
        var fileProtos = fdSet.File.ToList();
        var fileByteStrings = fileProtos.Select(f => f.ToByteString()).ToList();
        IReadOnlyList<FileDescriptor> descriptors;
        try
        {
            descriptors = FileDescriptor.BuildFromByteStrings(fileByteStrings);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"gRPC schema file '{path}' references imports that aren't in the FileDescriptorSet. " +
                $"Re-run protoc with --include_imports so every transitive .proto lands in the output.",
                ex);
        }

        return Build(descriptors, fileProtos, path);
    }

    /// <summary>Test-friendly overload operating on already-built descriptors.</summary>
    public static BowireRecording Build(
        IReadOnlyList<FileDescriptor> descriptors,
        IReadOnlyList<FileDescriptorProto>? fileProtos,
        string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var recording = new BowireRecording
        {
            Id = "grpc-schema-only",
            Name = "gRPC schema-only",
            Description =
                $"Generated from protobuf FileDescriptorSet '{sourceLabel}'. " +
                "Responses are synthesised from the message schema; override by capturing real recordings.",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordingFormatVersion = Kuestenlogik.Bowire.Mock.Loading.RecordingFormatVersion.Current
        };

        // Each step's schemaDescriptor carries the FULL
        // FileDescriptorSet (every .proto file the schema needs, not
        // just the one the method lives in) — that's what
        // DescriptorPool.BuildFrom expects when the mock boots gRPC
        // Reflection. Encoding all files once and reusing the base64
        // string per step avoids duplicated work.
        var schemaDescriptorB64 = fileProtos is not null && fileProtos.Count > 0
            ? EncodeSchemaDescriptor(fileProtos)
            : null;

        var stepNumber = 1;
        for (var i = 0; i < descriptors.Count; i++)
        {
            var file = descriptors[i];

            foreach (var service in file.Services)
            {
                foreach (var method in service.Methods)
                {
                    // For client-streaming / bidi methods we'd emit a
                    // stream of responses; reduce scope to unary +
                    // server-streaming for this slice so the replayer's
                    // existing two paths (ReplayGrpcAsync / ReplayGrpcStreamAsync)
                    // stay unchanged.
                    var sampleBytes = ProtobufSampleEncoder.Encode(method.OutputType);
                    var sampleB64 = Convert.ToBase64String(sampleBytes);

                    var isServerStreaming = method.IsServerStreaming;
                    var step = new BowireRecordingStep
                    {
                        Id = "grpc_schema_" + stepNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Protocol = "grpc",
                        Service = service.FullName,
                        Method = method.Name,
                        MethodType = isServerStreaming ? "ServerStreaming" : "Unary",
                        Status = "OK",
                        SchemaDescriptor = schemaDescriptorB64,
                        ResponseBinary = isServerStreaming ? null : sampleB64,
                        ReceivedMessages = isServerStreaming
                            ? new List<BowireRecordingFrame>
                            {
                                new() { Index = 0, TimestampMs = 0, ResponseBinary = sampleB64 },
                                new() { Index = 1, TimestampMs = 100, ResponseBinary = sampleB64 },
                                new() { Index = 2, TimestampMs = 200, ResponseBinary = sampleB64 }
                            }
                            : null
                    };

                    recording.Steps.Add(step);
                    stepNumber++;
                }
            }
        }

        if (recording.Steps.Count == 0)
        {
            throw new InvalidDataException(
                $"gRPC schema '{sourceLabel}' parsed but has no service methods. " +
                $"Make sure the .proto files define at least one `service {{ }}` block.");
        }

        return recording;
    }

    // DescriptorPool.BuildFrom expects each schemaDescriptor blob to
    // parse as a FileDescriptorSet containing every file the method's
    // types reference (transitives included). Since the caller already
    // handed us the full set, just serialize it back as a set.
    private static string EncodeSchemaDescriptor(IReadOnlyList<FileDescriptorProto> fileProtos)
    {
        var set = new FileDescriptorSet();
        foreach (var fp in fileProtos) set.File.Add(fp);
        return Convert.ToBase64String(set.ToByteArray());
    }
}

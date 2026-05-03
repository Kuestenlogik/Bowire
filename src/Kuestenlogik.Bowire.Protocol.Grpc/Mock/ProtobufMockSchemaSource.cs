// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Mock;

/// <summary>
/// gRPC plugin's contribution to the Bowire mock server's schema-only
/// flow. When the user runs <c>bowire mock --grpc-schema &lt;path&gt;</c>,
/// the mock server discovers this <see cref="IBowireMockSchemaSource"/>
/// (Kind = <c>"protobuf"</c>) and delegates the FileDescriptorSet →
/// recording conversion to <see cref="ProtobufRecordingBuilder"/>.
/// </summary>
/// <remarks>
/// Implementation moved from <c>Kuestenlogik.Bowire.Mock.Schema</c> to here as
/// part of the mock plugin-isation refactor —
/// <c>Google.Protobuf</c>, <c>Grpc.AspNetCore</c>, and
/// <c>Grpc.Reflection</c> now hang only on the gRPC plugin instead of
/// on <c>Kuestenlogik.Bowire.Mock</c>.
/// </remarks>
public sealed class ProtobufMockSchemaSource : IBowireMockSchemaSource
{
    /// <inheritdoc/>
    public string Kind => "protobuf";

    /// <inheritdoc/>
    public Task<BowireRecording> BuildAsync(string path, CancellationToken ct) =>
        ProtobufRecordingBuilder.LoadAsync(path, ct);
}

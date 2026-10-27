// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop;

/// <summary>The gRPC face of <see cref="InteropRpc"/>; nothing of its own.</summary>
internal sealed class InteropGrpcService : V1.Interop.InteropBase
{
    public override Task<ImportCollectionResponse> ImportCollection(ImportCollectionRequest request, ServerCallContext context) =>
        Task.FromResult(InteropRpc.ImportCollection(request));

    public override Task<ExportHttpResponse> ExportHttp(ExportHttpRequest request, ServerCallContext context) =>
        Task.FromResult(InteropRpc.ExportHttp(request));
}

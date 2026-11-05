// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.ManualEntry.V1;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.ManualEntry;

/// <summary>The ManualEntry gRPC service over the host's protocol registry.</summary>
internal sealed partial class ManualEntryGrpcService(ILogger<ManualEntryGrpcService> logger)
    : V1.ManualEntry.ManualEntryBase
{
    public override Task<ListManualEntryFormsResponse> ListManualEntryForms(
        ListManualEntryFormsRequest request, ServerCallContext context) =>
        Task.FromResult(ManualEntryRpc.List(
            BowireEndpointHelpers.GetRegistry().Protocols,
            request,
            (protocol, problems) => LogRejected(logger, protocol, string.Join("; ", problems))));

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The hand-entry form of protocol '{Protocol}' is not served: {Problems}")]
    private static partial void LogRejected(ILogger logger, string protocol, string problems);
}

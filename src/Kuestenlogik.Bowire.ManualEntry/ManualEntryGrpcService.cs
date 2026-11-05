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
    public override async Task<ListManualEntryFormsResponse> ListManualEntryForms(
        ListManualEntryFormsRequest request, ServerCallContext context)
    {
        var registry = BowireEndpointHelpers.GetRegistry();
        // #1003 - a sidecar's form lives in a process that is started lazily,
        // like its settings: give every plugin that needs preparation the
        // chance first, as /api/protocols does. One that will not start
        // describes nothing.
        foreach (var deferred in registry.Protocols.OfType<Plugins.IBowireDeferredSettings>())
            await deferred.PrepareSettingsAsync(context.CancellationToken).ConfigureAwait(false);
        return ManualEntryRpc.List(
            registry.Protocols,
            request,
            (protocol, problems) => LogRejected(logger, protocol, string.Join("; ", problems)));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The hand-entry form of protocol '{Protocol}' is not served: {Problems}")]
    private static partial void LogRejected(ILogger logger, string protocol, string problems);
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Recordings.Correlation;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Recordings;

/// <summary>
/// Discoverable endpoint-mount entry point for the Recordings rail
/// (#539). Picked up by Core's <c>BowireApiEndpoints</c> scan via the
/// <see cref="IBowireEndpointContribution"/> seam, so the correlation
/// endpoint inherits the auth-gated route group and the host's base
/// path without core knowing this package exists.
/// </summary>
public sealed class BowireRecordingsEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireRecordingCorrelationEndpoints(basePath);
    }
}

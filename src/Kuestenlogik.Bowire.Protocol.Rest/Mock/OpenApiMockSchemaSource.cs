// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Rest.Mock;

/// <summary>
/// REST plugin's contribution to the Bowire mock server's schema-only
/// flow. When the user runs <c>bowire mock --schema &lt;path&gt;</c>,
/// the mock server discovers this <see cref="IBowireMockSchemaSource"/>
/// (Kind = <c>"openapi"</c>) and delegates the schema → recording
/// conversion to <see cref="OpenApiRecordingBuilder"/>.
/// </summary>
/// <remarks>
/// Implementation moved from <c>Kuestenlogik.Bowire.Mock.Schema</c> to here as
/// part of the mock plugin-isation refactor — the
/// <c>Microsoft.OpenApi.Readers</c> dependency now hangs only on the
/// REST plugin (where it was already needed for endpoint discovery)
/// instead of on <c>Kuestenlogik.Bowire.Mock</c>.
/// </remarks>
public sealed class OpenApiMockSchemaSource : IBowireMockSchemaSource
{
    /// <inheritdoc/>
    public string Kind => "openapi";

    /// <inheritdoc/>
    public Task<BowireRecording> BuildAsync(string path, CancellationToken ct) =>
        OpenApiRecordingBuilder.LoadAsync(path, ct);
}

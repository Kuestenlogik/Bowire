// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;

/// <summary>
/// <see cref="IBowireOpenApiAdapter"/> implementation backed by
/// <c>Microsoft.OpenApi</c> 3.x. Bridges the version-agnostic Bowire
/// REST plugin to the modern OpenAPI library API. Found automatically
/// by <see cref="BowireOpenApiAdapterRegistry"/> when this assembly is
/// loaded; embedded hosts that want to pin a different
/// <c>Microsoft.OpenApi</c> line reference
/// <c>Kuestenlogik.Bowire.Protocol.Rest.OpenApi2</c> instead.
/// </summary>
public sealed class OpenApi3Adapter : IBowireOpenApiAdapter
{
    /// <inheritdoc/>
    public int OpenApiLibraryMajorVersion => 3;

    /// <inheritdoc/>
    public async Task<BowireOpenApiDiscoveryResult?> FetchAndDiscoverAsync(
        string docUrl, HttpClient http, CancellationToken ct)
    {
        var parsed = await OpenApiDiscovery.FetchAndParseAsync(docUrl, http, ct).ConfigureAwait(false);
        if (parsed?.Document is null) return null;
        var apiBaseUrl = OpenApiDiscovery.GetFirstServerUrl(parsed.Document);
        var services = OpenApiDiscovery.BuildServices(parsed.Document);
        return new BowireOpenApiDiscoveryResult(
            SourceUrl: docUrl,
            ApiBaseUrl: apiBaseUrl,
            Services: services,
            RawContent: parsed.RawText);
    }

    /// <inheritdoc/>
    public async Task<BowireOpenApiDiscoveryResult?> ParseAndDiscoverAsync(
        string content, string sourceLabel, CancellationToken ct)
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(content, ct).ConfigureAwait(false);
        if (parsed?.Document is null) return null;
        var apiBaseUrl = OpenApiDiscovery.GetFirstServerUrl(parsed.Document);
        var services = OpenApiDiscovery.BuildServices(parsed.Document);
        return new BowireOpenApiDiscoveryResult(
            SourceUrl: sourceLabel,
            ApiBaseUrl: apiBaseUrl,
            Services: services,
            RawContent: content);
    }

    /// <inheritdoc/>
    public Task<BowireRecording> BuildMockRecordingFromFileAsync(string path, CancellationToken ct)
        => OpenApiRecordingBuilder.LoadAsync(path, ct);
}

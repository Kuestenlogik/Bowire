// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2;

/// <summary>
/// <see cref="IBowireOpenApiAdapter"/> implementation backed by
/// <c>Microsoft.OpenApi</c> 2.x — the .NET 10 ecosystem default
/// (ASP.NET Core 10's <c>AddOpenApi()</c> pins this line). Embedded
/// hosts that mix Bowire's REST plugin with their own ASP.NET OpenAPI
/// pipeline should reference this package instead of
/// <c>Kuestenlogik.Bowire.Protocol.Rest.OpenApi3</c> to keep a single
/// <c>Microsoft.OpenApi.dll</c> version in the process.
/// </summary>
public sealed class OpenApi2Adapter : IBowireOpenApiAdapter
{
    /// <inheritdoc/>
    public int OpenApiLibraryMajorVersion => 2;

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

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Seam between the REST protocol plugin and the OpenAPI parsing
/// library. Bowire.Protocol.Rest itself has NO compile-time reference
/// to <c>Microsoft.OpenApi</c>; the actual parsing implementation
/// lives in one of the optional adapter packages
/// (<c>Kuestenlogik.Bowire.Protocol.Rest.OpenApi3</c>, future
/// <c>.OpenApi2</c>), discovered at runtime via
/// <see cref="BowireOpenApiAdapterRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why a seam at all. <c>Microsoft.OpenApi</c> had a major version
/// bump from 2.x to 3.x with a redesigned API surface. ASP.NET Core
/// 10's built-in <c>AddOpenApi()</c> still depends on the 2.x line;
/// embedded hosts that mix Bowire's REST plugin with ASP.NET's own
/// OpenAPI generator would otherwise hit a single-DLL-version collision
/// at runtime. Splitting the parser into version-specific adapter
/// packages lets each consumer pick the version that matches the rest
/// of their app — the REST plugin stays version-agnostic and falls back
/// to "no schema discovery, no mock-from-OpenAPI" when no adapter is
/// registered.
/// </para>
/// <para>
/// Public contract: every method returns Bowire's own DTOs
/// (<see cref="BowireServiceInfo"/>, <see cref="BowireRecording"/>),
/// never leaking <c>Microsoft.OpenApi.*</c> types across the seam.
/// </para>
/// </remarks>
public interface IBowireOpenApiAdapter
{
    /// <summary>
    /// Major version of the <c>Microsoft.OpenApi</c> library this
    /// adapter is built against. Used by
    /// <see cref="BowireOpenApiAdapterRegistry"/> to pick the right
    /// implementation when more than one adapter package is loaded
    /// (rare — typically only when a sideloaded plugin pulls in a
    /// second adapter). The registry prefers the adapter whose
    /// <see cref="OpenApiLibraryMajorVersion"/> matches the
    /// <c>Microsoft.OpenApi</c> assembly already loaded into the
    /// process; falls back to the lowest-numbered candidate when no
    /// match is available.
    /// </summary>
    int OpenApiLibraryMajorVersion { get; }

    /// <summary>
    /// Fetch the OpenAPI document at <paramref name="docUrl"/> via
    /// <paramref name="http"/> and convert it into a list of Bowire
    /// services + the API base URL the workbench should fire requests
    /// at. Returns <c>null</c> when the URL isn't reachable, returns
    /// non-OpenAPI content, or the parse otherwise fails — the REST
    /// plugin treats null as "this URL isn't an OpenAPI doc" and lets
    /// sibling protocol plugins try the same URL.
    /// </summary>
    Task<BowireOpenApiDiscoveryResult?> FetchAndDiscoverAsync(
        string docUrl,
        HttpClient http,
        CancellationToken ct);

    /// <summary>
    /// Parse OpenAPI/Swagger document text (JSON or YAML) without an
    /// HTTP fetch. Used for documents uploaded via the workbench's
    /// schema upload surface or pasted into the discovery prompt.
    /// </summary>
    /// <param name="content">Verbatim document body — JSON or YAML.</param>
    /// <param name="sourceLabel">Display label for the document; surfaces in caches and recordings.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BowireOpenApiDiscoveryResult?> ParseAndDiscoverAsync(
        string content,
        string sourceLabel,
        CancellationToken ct);

    /// <summary>
    /// Read an OpenAPI document from disk and synthesise a
    /// <see cref="BowireRecording"/> with one step per operation +
    /// schema-generated example responses. Drives
    /// <c>bowire mock --schema &lt;path&gt;</c>.
    /// </summary>
    Task<BowireRecording> BuildMockRecordingFromFileAsync(
        string path,
        CancellationToken ct);
}

/// <summary>
/// Cross-version discovery result. Carries the parsed Bowire service
/// model plus enough metadata for the REST plugin's invocation /
/// recording / schema-cache paths to do their job without seeing the
/// underlying OpenAPI document object.
/// </summary>
/// <param name="SourceUrl">URL or label the document was loaded from.</param>
/// <param name="ApiBaseUrl">
/// First server URL declared in the document (with template variables
/// expanded against their defaults), or <c>null</c> when the document
/// doesn't declare one.
/// </param>
/// <param name="Services">Discovered Bowire services.</param>
/// <param name="RawContent">
/// Verbatim document text — stamped into the workbench's source-schema
/// cache so a downstream recording carries the original contract for
/// the mock host to serve verbatim under <c>/openapi.{json,yaml}</c>.
/// </param>
public sealed record BowireOpenApiDiscoveryResult(
    string SourceUrl,
    string? ApiBaseUrl,
    List<BowireServiceInfo> Services,
    string? RawContent);

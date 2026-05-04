// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.SignalR;

/// <summary>
/// Bowire protocol plugin for ASP.NET SignalR Hubs.
/// Discovers hubs via <see cref="Microsoft.AspNetCore.SignalR.HubMetadata"/> in embedded mode.
/// Auto-discovered by <see cref="BowireProtocolRegistry"/>.
/// </summary>
public sealed class BowireSignalRProtocol : IBowireProtocol
{
    private IServiceProvider? _serviceProvider;
    /// <summary>
    /// Application config picked up at <see cref="Initialize"/>. The plugin
    /// hands this to <see cref="LocalhostCertTrust.IsTrustedFor"/> on every
    /// connect call so changes to <c>Bowire:TrustLocalhostCert</c> /
    /// <c>Bowire:signalr:TrustLocalhostCert</c> at runtime take effect
    /// without a host restart (IConfiguration is reload-on-change-aware
    /// when the host wires it up).
    /// </summary>
    private IConfiguration? _configuration;

    public string Name => "SignalR";
    public string Id => "signalr";

    // Azure SignalR Service — official Microsoft Azure Architecture Icons (2025-11 set).
    public string IconSvg => """<svg viewBox="0 0 18 18" width="16" height="16" xmlns="http://www.w3.org/2000/svg" aria-hidden="true"><defs><radialGradient id="signalr-tab-grad" cx="9" cy="9" r="8.5" gradientUnits="userSpaceOnUse"><stop offset="0.18" stop-color="#5ea0ef"/><stop offset="1" stop-color="#0078d4"/></radialGradient><clipPath id="signalr-tab-clip"><path d="M14.21,15.72A8.5,8.5,0,0,1,3.79,2.28l.09-.06a8.5,8.5,0,0,1,10.33,13.5" fill="none"/></clipPath></defs><path d="M14.21,15.72A8.5,8.5,0,0,1,3.79,2.28l.09-.06a8.5,8.5,0,0,1,10.33,13.5" fill="url(#signalr-tab-grad)"/><g clip-path="url(#signalr-tab-clip)"><path d="M4.13,7.05a.28.28,0,0,0,.2.48h6.12A1.55,1.55,0,0,1,11.6,8a1.61,1.61,0,0,1,.43.92,1.43,1.43,0,0,1-.36,1.15,1.41,1.41,0,0,1-1.12.54H8.44a.08.08,0,0,0-.09.06L7.81,12c-.12.29-.25.59-.37.89a.08.08,0,0,0,0,.09L9,14.48l2.59,2.59.46.49,2.14-1.19L13.72,16l-1.43-1.44L10.74,13l-.07,0,0,0,.52-.07A3.84,3.84,0,0,0,14,10.65a3.85,3.85,0,0,0,0-3.08,3.93,3.93,0,0,0-.73-1.12,3.67,3.67,0,0,0-1.24-.89,4,4,0,0,0-1.66-.34h-3V4.05A.14.14,0,0,0,7.18,4Z" fill="#f2f2f2"/></g></svg>""";

    /// <summary>
    /// Allows injection of the application's <see cref="IServiceProvider"/>
    /// for embedded hub discovery via endpoint data sources.
    /// </summary>
    public void Initialize(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _configuration = serviceProvider?.GetService<IConfiguration>();
    }

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct)
    {
        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider);

        if (services.Count == 0)
        {
            // No hubs discovered — return empty list rather than error.
            // This is expected in standalone mode where we can't scan endpoints.
            return Task.FromResult<List<BowireServiceInfo>>([]);
        }

        return Task.FromResult(services);
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var hubUrl = ResolveHubUrl(serverUrl, service);

        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var sanitisedMetadata = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);

        await using var invoker = new SignalRInvoker();
        var trustLocalhost = LocalhostCertTrust.IsTrustedFor(_configuration, Id, hubUrl);
        await invoker.ConnectAsync(hubUrl, sanitisedMetadata, mtlsConfig, ct, trustLocalhost);
        return await invoker.InvokeAsync(method, jsonMessages, ct);
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var hubUrl = ResolveHubUrl(serverUrl, service);

        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var sanitisedMetadata = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);

        await using var invoker = new SignalRInvoker();
        var trustLocalhost = LocalhostCertTrust.IsTrustedFor(_configuration, Id, hubUrl);
        await invoker.ConnectAsync(hubUrl, sanitisedMetadata, mtlsConfig, ct, trustLocalhost);

        await foreach (var response in invoker.StreamAsync(method, jsonMessages, ct))
            yield return response;
    }

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var hubUrl = ResolveHubUrl(serverUrl, service);

        // Look up method info to determine streaming direction
        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider);
        var svc = services.FirstOrDefault(s => s.Name == service || s.Package == service);
        var methodInfo = svc?.Methods.FirstOrDefault(m => m.Name == method);

        var isClientStreaming = methodInfo?.ClientStreaming ?? true;
        var isServerStreaming = methodInfo?.ServerStreaming ?? true;

        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var sanitisedMetadata = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);

        var trustLocalhost = LocalhostCertTrust.IsTrustedFor(_configuration, Id, hubUrl);
        return await SignalRBowireChannel.CreateAsync(
            hubUrl, method, isClientStreaming, isServerStreaming, headers: sanitisedMetadata, ct, mtlsConfig, trustLocalhost);
    }

    /// <summary>
    /// Resolves the full hub URL. The service's Package field contains the hub path
    /// (e.g. "/chatHub"). Combine with the server base URL.
    /// </summary>
    private static string ResolveHubUrl(string serverUrl, string service)
    {
        // Service might be the hub name (e.g. "ChatHub") or the path (e.g. "/chatHub")
        var path = service.StartsWith('/') ? service : $"/{service}";
        var baseUrl = serverUrl.TrimEnd('/');
        return $"{baseUrl}{path}";
    }
}

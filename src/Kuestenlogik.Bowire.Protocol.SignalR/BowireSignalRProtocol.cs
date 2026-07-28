// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
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
    public string Description => "ASP.NET Core SignalR hubs over WebSocket / Server-Sent-Events / long-polling.";
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

    /// <summary>
    /// Side-channel marker the discovery endpoint appends to the URL when
    /// the caller explicitly hinted <c>signalr@…</c>. DiscoverAsync has no
    /// hint parameter, and the ad-hoc fallback below must never fire on
    /// the hint-less all-plugins fan-out (every http(s) URL would get a
    /// negotiate probe, and any hub-shaped answer would grow a phantom
    /// service next to the owning plugin's real one). Mirrors the gRPC
    /// transport / SSE ad-hoc markers; must stay aligned with the literal
    /// in BowireDiscoveryEndpoints.
    /// </summary>
    internal const string AdHocHintMarker = "__bowireSignalRAdHoc=1";

    /// <summary>
    /// Service name of the synthesised separate-target hub surface. The
    /// space makes collisions with real hub class names impossible, so
    /// the invoke paths can safely key their ad-hoc redirect on it.
    /// </summary>
    internal const string AdHocServiceName = "SignalR Hub";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct)
    {
        // Strip the hint marker before anything else touches the URL —
        // the self-origin check, the negotiate probe, OriginUrl and the
        // hub metadata scan must all see the clean URL.
        var hinted = false;
        if (!string.IsNullOrEmpty(serverUrl))
        {
            var marked = serverUrl;
            serverUrl = serverUrl
                .Replace("?" + AdHocHintMarker, "", StringComparison.Ordinal)
                .Replace("&" + AdHocHintMarker, "", StringComparison.Ordinal);
            hinted = marked.Length != serverUrl.Length;
        }

        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider, serverUrl);

        // Separate-target mode (`bowire --url signalr@http://host/hub`):
        // hub metadata only exists in the embedded host's endpoint data
        // sources, so an external URL yields nothing even though the
        // plugin ran — the documented standalone flow dead-ended in a
        // 502 (#510). SignalR has no wire-level reflection to list hub
        // methods, but the negotiate handshake confirms hub-ness; on a
        // confirmed hub we expose generic `invoke` / `stream` entry
        // points whose payload names the hub method explicitly.
        if (hinted
            && services.Count == 0
            && IsHttpUrl(serverUrl)
            && !Helpers.SelfOriginCheck.IsSelfOrigin(serverUrl, _serviceProvider)
            && await NegotiateSucceedsAsync(serverUrl, ct).ConfigureAwait(false))
        {
            services.Add(BuildAdHocService(serverUrl));
        }

        foreach (var svc in services)
            svc.OriginUrl ??= serverUrl;

        return services;
    }

    /// <summary>
    /// Probes <c>POST {url}/negotiate?negotiateVersion=1</c> and reports
    /// whether the answer is a SignalR negotiate payload. Own 4 s
    /// deadline — discovery must not hang on a stalling server.
    /// </summary>
    private async Task<bool> NegotiateSucceedsAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(4));
            var negotiateUrl = serverUrl.TrimEnd('/') + "/negotiate?negotiateVersion=1";
            // Per-probe client instead of a long-lived field: discovery
            // probes are rare, and this keeps the plugin free of an
            // owned-IDisposable member. The factory wires the
            // TrustLocalhostCert opt-in the same way the invoke paths do.
            using var http = BowireHttpClientFactory.Create(_configuration, Id, TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, negotiateUrl);
            using var response = await http.SendAsync(request, probeCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync(probeCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty("connectionId", out _)
                    || doc.RootElement.TryGetProperty("negotiateVersion", out _)
                    || doc.RootElement.TryGetProperty("availableTransports", out _));
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
        catch (JsonException) { return false; }
        catch (UriFormatException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsHttpUrl(string url) =>
        !string.IsNullOrEmpty(url) &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the separate-target hub surface: one Unary <c>invoke</c> and
    /// one ServerStreaming <c>stream</c> method whose payload carries the
    /// real hub method name plus JSON-encoded positional arguments —
    /// SignalR has no reflection wire, so the operator supplies what
    /// discovery cannot know.
    /// </summary>
    private static BowireServiceInfo BuildAdHocService(string serverUrl)
    {
        static BowireMessageInfo BuildAdHocInput() => new("HubCall", "signalr.HubCall",
        [
            new BowireFieldInfo("method", 1, "string", "LABEL_REQUIRED", false, false, null, null)
            {
                Source = "body",
                Description = "Hub method to call, e.g. SendMessage."
            },
            new BowireFieldInfo("args", 2, "string", "LABEL_OPTIONAL", false, true, null, null)
            {
                Source = "body",
                Description = "Positional arguments, one JSON value per entry: 42, \"text\", {\"x\":1}. Bare words are sent as strings."
            }
        ]);

        var output = new BowireMessageInfo("HubResult", "signalr.HubResult", []);

        return new BowireServiceInfo(
            Name: AdHocServiceName,
            Package: ExtractPath(serverUrl),
            Methods:
            [
                new BowireMethodInfo(
                    Name: "invoke",
                    FullName: $"{AdHocServiceName}/invoke",
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: BuildAdHocInput(),
                    OutputType: output,
                    MethodType: "Unary")
                {
                    Summary = "Invoke a hub method and await its result",
                    Description = "Calls the named hub method on " + serverUrl + " and returns its result. SignalR exposes no method list over the wire — name the method yourself."
                },
                new BowireMethodInfo(
                    Name: "stream",
                    FullName: $"{AdHocServiceName}/stream",
                    ClientStreaming: false,
                    ServerStreaming: true,
                    InputType: BuildAdHocInput(),
                    OutputType: output,
                    MethodType: "ServerStreaming")
                {
                    Summary = "Subscribe to a streaming hub method",
                    Description = "Streams from the named hub method (IAsyncEnumerable / ChannelReader) on " + serverUrl + "."
                }
            ])
        { Source = "signalr", Description = "Ad-hoc SignalR hub — negotiate handshake succeeded; hub methods are supplied per call." };
    }

    private static string ExtractPath(string url)
    {
        try
        {
            return new Uri(url).PathAndQuery;
        }
        catch (UriFormatException)
        {
            return "/";
        }
    }

    /// <summary>
    /// Parses the ad-hoc payload <c>{"method": "...", "args": [...]}</c>
    /// into the target hub method plus positional arguments. Returns an
    /// error string (instead of throwing) so the invoke paths can surface
    /// it as a normal failed <see cref="InvokeResult"/>.
    /// </summary>
    internal static (string? HubMethod, object?[] Args, string? Error) ParseAdHocPayload(
        List<string> jsonMessages)
    {
        const string usage = "Ad-hoc SignalR calls need a payload like {\"method\": \"SendMessage\", \"args\": [\"hello\"]}.";
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return (null, [], usage);

        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, [], usage);

            if (!doc.RootElement.TryGetProperty("method", out var methodProp)
                || methodProp.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(methodProp.GetString()))
            {
                return (null, [], "Missing hub method name. " + usage);
            }

            var args = new List<object?>();
            if (doc.RootElement.TryGetProperty("args", out var argsProp))
            {
                if (argsProp.ValueKind != JsonValueKind.Array)
                    return (null, [], "\"args\" must be an array. " + usage);
                foreach (var el in argsProp.EnumerateArray())
                {
                    // The form pane's repeated-string field delivers every
                    // entry as a JSON string — re-parse its content so a
                    // typed `42` / `true` / `{"x":1}` reaches the hub with
                    // its real type; anything unparseable stays the raw
                    // string ("bare words are sent as strings").
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var raw = el.GetString() ?? "";
                        try
                        {
                            using var inner = JsonDocument.Parse(raw);
                            args.Add(SignalRInvoker.JsonElementToArg(inner.RootElement.Clone()));
                        }
                        catch (JsonException)
                        {
                            args.Add(raw);
                        }
                    }
                    else
                    {
                        args.Add(SignalRInvoker.JsonElementToArg(el));
                    }
                }
            }

            return (methodProp.GetString(), [.. args], null);
        }
        catch (JsonException)
        {
            return (null, [], "Payload is not valid JSON. " + usage);
        }
    }

    private static bool IsAdHocService(string service) =>
        string.Equals(service, AdHocServiceName, StringComparison.Ordinal);

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var hubUrl = ResolveHubUrl(serverUrl, service);

        // Ad-hoc separate-target surface: the workbench method is the
        // generic `invoke`; the real hub method + args live in the
        // payload (SignalR has no reflection wire — see #510).
        string targetMethod = method;
        object?[]? adHocArgs = null;
        if (IsAdHocService(service))
        {
            var (hubMethod, args, error) = ParseAdHocPayload(jsonMessages);
            if (error is not null)
                return new InvokeResult(error, 0, "Error", []);
            targetMethod = hubMethod!;
            adHocArgs = args;
        }

        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var sanitisedMetadata = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);

        await using var invoker = new SignalRInvoker();
        var trustLocalhost = LocalhostCertTrust.IsTrustedFor(_configuration, Id, hubUrl);
        await invoker.ConnectAsync(hubUrl, sanitisedMetadata, mtlsConfig, ct, trustLocalhost);
        return adHocArgs is not null
            ? await invoker.InvokeWithArgsAsync(targetMethod, adHocArgs, ct)
            : await invoker.InvokeAsync(
                targetMethod, jsonMessages, ct, ParameterCountOf(serverUrl, service, targetMethod));
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var hubUrl = ResolveHubUrl(serverUrl, service);

        // Ad-hoc redirect — same shape as InvokeAsync above.
        string targetMethod = method;
        object?[]? adHocArgs = null;
        if (IsAdHocService(service))
        {
            var (hubMethod, args, error) = ParseAdHocPayload(jsonMessages);
            if (error is not null)
                throw new InvalidOperationException(error);
            targetMethod = hubMethod!;
            adHocArgs = args;
        }

        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var sanitisedMetadata = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);

        await using var invoker = new SignalRInvoker();
        var trustLocalhost = LocalhostCertTrust.IsTrustedFor(_configuration, Id, hubUrl);
        await invoker.ConnectAsync(hubUrl, sanitisedMetadata, mtlsConfig, ct, trustLocalhost);

        var stream = adHocArgs is not null
            ? invoker.StreamWithArgsAsync(targetMethod, adHocArgs, ct)
            : invoker.StreamAsync(
                targetMethod, jsonMessages, ct, ParameterCountOf(serverUrl, service, targetMethod));
        await foreach (var response in stream)
            yield return response;
    }

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        // The ad-hoc surface is Unary + ServerStreaming only and its hub
        // method name travels in the request payload — a channel opens on
        // a method name known up front, which would invoke the literal
        // "invoke"/"stream" on the hub. Route ad-hoc calls through the
        // invoke/stream APIs instead.
        if (IsAdHocService(service)) return null;

        var hubUrl = ResolveHubUrl(serverUrl, service);

        // Look up method info to determine streaming direction
        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider, serverUrl);
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
    /// How many parameters the named hub method declares, from the
    /// embedded discovery scan — <c>null</c> when the method isn't known
    /// (separate-target mode, where there is no hub metadata to read).
    ///
    /// The invoker needs it to decide whether a one-object form payload
    /// is "one arg per property" or "one complex argument": {"text":"hi"}
    /// against Echo(string text) is the former, against Send(Dto d) with
    /// a text field the latter. Without the arity a single-parameter hub
    /// method received the wrapper object and failed with HubException.
    /// </summary>
    private int? ParameterCountOf(string serverUrl, string service, string method)
    {
        if (IsAdHocService(service)) return null;
        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider, serverUrl);
        var svc = services.FirstOrDefault(s => s.Name == service || s.Package == service);
        var info = svc?.Methods.FirstOrDefault(m => m.Name == method);
        return info?.InputType?.Fields?.Count;
    }

    /// <summary>
    /// Resolves the full hub URL. Discovery returns the hub path in the
    /// service's Package field (e.g. <c>/hubs/port-calls</c> when the host
    /// did <c>app.MapHub&lt;PortCallHub&gt;("/hubs/port-calls")</c>) and
    /// the class name in Name (<c>PortCallHub</c>). The invoke endpoint
    /// passes whichever the caller supplied. We look up the discovered
    /// service so we always pick the configured Package — falling back
    /// to the literal name only when discovery has nothing to say
    /// (standalone CLI without an app to scan).
    /// </summary>
    private string ResolveHubUrl(string serverUrl, string service)
    {
        // Ad-hoc separate-target service: the connection URL already IS
        // the hub URL (its path fed the service's Package) — appending
        // anything would double the path.
        if (IsAdHocService(service)) return serverUrl;

        var services = SignalRHubDiscovery.DiscoverHubs(_serviceProvider, serverUrl);
        var svc = services.FirstOrDefault(s => s.Name == service || s.Package == service);
        var raw = !string.IsNullOrEmpty(svc?.Package) ? svc.Package : service;
        var path = raw.StartsWith('/') ? raw : $"/{raw}";
        var baseUrl = serverUrl.TrimEnd('/');
        return $"{baseUrl}{path}";
    }
}

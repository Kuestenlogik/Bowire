// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Neuroglia.AsyncApi;
using Neuroglia.AsyncApi.v3;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// AsyncAPI as a Bowire discovery source. Implements <see cref="IBowireProtocol"/>
/// so it slots into the existing assembly-scanned registry, URL-hint routing
/// (<c>asyncapi@./doc.yaml</c>), and recording/mock plumbing — but it does not
/// own a wire. <see cref="DiscoverAsync"/> parses an AsyncAPI document and
/// turns its channels into <see cref="BowireServiceInfo"/> nodes; the actual
/// publish/subscribe calls in <see cref="InvokeAsync"/> are routed to the
/// matching wire plugin (MQTT, Kafka, WebSocket, …) at runtime via
/// <c>BowireProtocolRegistry</c>.
///
/// Phase A2: the loader is wired up — <see cref="DiscoverAsync"/> reads an
/// AsyncAPI 3.0 document, walks its channels, and emits one
/// <see cref="BowireServiceInfo"/> per channel. The binding-translator layer
/// (which routes invocations to MQTT / Kafka / WebSocket via
/// <c>BowireProtocolRegistry</c>) lands in Phase A3 — until then,
/// <see cref="InvokeAsync"/> still raises <see cref="NotSupportedException"/>.
/// AsyncAPI 2.x parsing is supported by the SDK reader but not yet mapped
/// here; only V3 documents produce services.
/// </summary>
public sealed class BowireAsyncApiProtocol : IBowireProtocol
{
    // Static loader — Neuroglia's reader is stateless and the DI graph
    // boilerplate is cheap to amortise across discovery calls.
    private static readonly AsyncApiDocumentLoader Loader = new();

    public string Name => "AsyncAPI";

    public string Id => "asyncapi";

    public string IconSvg =>
        // Placeholder glyph — three stacked horizontal lines with a small
        // arrow head suggesting "channels with messages flowing". Will be
        // swapped for the official AsyncAPI mark (subject to licensing
        // check) when the UI surface lands.
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h12"/><path d="M4 12h8"/><path d="M4 17h14"/><path d="M18 7l3 0"/><path d="M15 12l3 0"/><path d="M21 17l-3 -3"/><path d="M21 17l-3 3"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // Early-out for any URL that doesn't look like an AsyncAPI source.
        // Discovery probes every plugin against every `bowire --url X`
        // invocation, and the AsyncAPI loader would otherwise try to
        // HTTP-GET arbitrary gRPC / REST / MQTT endpoints just to fail
        // on parse. The extension check matches the OpenAPI plugin's
        // habit of only engaging when the URL ends in .yaml/.json.
        if (!AsyncApiDocumentLoader.LooksLikeAsyncApiSource(serverUrl))
        {
            return [];
        }

        IAsyncApiDocument document;
        try
        {
            document = await Loader.LoadAsync(serverUrl, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (HttpRequestException) { return []; }

        // Phase A2 only maps V3 documents; V2 support arrives later.
        if (document is not V3AsyncApiDocument v3)
        {
            return [];
        }

        return MapV3Channels(v3, serverUrl);
    }

    /// <summary>
    /// V3 channel walker. Each channel becomes one <see cref="BowireServiceInfo"/>;
    /// the service Name is the channel key (e.g. <c>lightingMeasured</c>) and
    /// the Package mirrors the document's <c>info.title</c> so multiple
    /// AsyncAPI documents in the same workbench session don't collide. One
    /// placeholder method per channel goes in for now — Phase A3 replaces it
    /// with one method per operation that targets this channel, picked from
    /// <c>document.Operations</c>.
    /// </summary>
    private static List<BowireServiceInfo> MapV3Channels(V3AsyncApiDocument document, string sourceUrl)
    {
        var apiTitle = document.Info?.Title ?? "AsyncAPI";
        var apiVersion = document.Info?.Version;
        var services = new List<BowireServiceInfo>();
        if (document.Channels is null) return services;

        foreach (var (channelKey, channel) in document.Channels)
        {
            var methodName = channel.Address ?? channelKey;
            // Placeholder method — full mapping (one per operation, with
            // send/receive direction wired onto ClientStreaming /
            // ServerStreaming) lands in Phase A3.
            var method = new BowireMethodInfo(
                Name: methodName,
                FullName: $"{apiTitle}.{channelKey}",
                ClientStreaming: false,
                ServerStreaming: true,
                InputType: new BowireMessageInfo(methodName + "Request", methodName + "Request", []),
                OutputType: new BowireMessageInfo(methodName + "Response", methodName + "Response", []),
                MethodType: "asyncapi-channel")
            {
                Summary = channel.Title ?? channel.Summary,
                Description = channel.Description
            };

            services.Add(new BowireServiceInfo(
                Name: channelKey,
                Package: apiTitle,
                Methods: [method])
            {
                Source = "asyncapi",
                Description = document.Info?.Description,
                Version = apiVersion,
                OriginUrl = sourceUrl,
                IsUploaded = false
            });
        }

        return services;
    }

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AsyncAPI discovery source has not loaded any channels yet — " +
            "the Phase A loader is not wired up. Once it is, InvokeAsync " +
            "will dispatch to the wire plugin named by the channel's binding.");
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw new NotSupportedException(
            "AsyncAPI discovery source has not loaded any channels yet — " +
            "InvokeStreamAsync will be wired through the binding-resolver " +
            "layer (Phase A2).");
#pragma warning disable CS0162 // Unreachable code — the yield keeps the
                              // method an iterator so the signature stays
                              // an IAsyncEnumerable<string>.
        yield break;
#pragma warning restore CS0162
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // Streaming channels arrive with Phase B (WebSocket + Kafka
        // bindings). Phase A only covers MQTT publish/subscribe, which
        // goes through InvokeAsync / InvokeStreamAsync.
        return Task.FromResult<IBowireChannel?>(null);
    }
}

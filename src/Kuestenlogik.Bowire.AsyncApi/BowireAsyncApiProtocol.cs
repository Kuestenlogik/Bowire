// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

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
/// Phase A scaffolding: this is a stub. <see cref="DiscoverAsync"/> returns an
/// empty list so the plugin is a no-op when scanned into the registry; the
/// loader + binding-translator layer lands in subsequent commits.
/// </summary>
public sealed class BowireAsyncApiProtocol : IBowireProtocol
{
    public string Name => "AsyncAPI";

    public string Id => "asyncapi";

    public string IconSvg =>
        // Placeholder glyph — three stacked horizontal lines with a small
        // arrow head suggesting "channels with messages flowing". Will be
        // swapped for the official AsyncAPI mark (subject to licensing
        // check) when the UI surface lands.
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h12"/><path d="M4 12h8"/><path d="M4 17h14"/><path d="M18 7l3 0"/><path d="M15 12l3 0"/><path d="M21 17l-3 -3"/><path d="M21 17l-3 3"/></svg>""";

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // Stub — real loader lands in Phase A2 (parse asyncapi.yaml, walk
        // channels + operations, emit one BowireServiceInfo per channel).
        // Returning empty keeps the plugin a no-op in the auto-scan
        // registry until the loader is wired up.
        return Task.FromResult(new List<BowireServiceInfo>());
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

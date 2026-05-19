// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Neuroglia.AsyncApi;
using Neuroglia.AsyncApi.IO;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Thin wrapper around <see cref="IAsyncApiDocumentReader"/> from the official
/// AsyncAPI .NET SDK (Neuroglia.AsyncApi.IO). Centralises:
///   1. The DI registration boilerplate (`AddAsyncApiIO`) — the reader
///      depends on JSON + YAML serializers which the SDK wires up.
///   2. The "load from file or HTTP URL" split so callers can hand over
///      either form without inspecting the input themselves.
///
/// One <see cref="AsyncApiDocumentLoader"/> instance is held statically by
/// <see cref="BowireAsyncApiProtocol"/> for the lifetime of the process; the
/// underlying SDK reader is stateless and thread-safe.
/// </summary>
internal sealed class AsyncApiDocumentLoader
{
    private readonly IServiceProvider _services;

    public AsyncApiDocumentLoader()
    {
        // Minimal isolated container — Bowire's main DI graph doesn't need
        // to know about AsyncAPI to keep the embedded-mode story modular.
        var services = new ServiceCollection();
        services.AddAsyncApiIO();
        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// True when <paramref name="urlOrPath"/> looks like something this loader
    /// can resolve — local file with an AsyncAPI-ish extension, or an HTTP(S)
    /// URL pointing at one. Cheap check used by <see cref="BowireAsyncApiProtocol"/>
    /// to bail out early so non-AsyncAPI discovery probes don't waste a network
    /// round-trip on every `bowire --url` invocation.
    /// </summary>
    public static bool LooksLikeAsyncApiSource(string urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return false;
        var trimmed = urlOrPath.Trim();
        // Extension check (covers ./asyncapi.yaml, ./events.json,
        // file:///etc/bowire/events.yml, and any http(s) URL ending in
        // the same suffixes). OrdinalIgnoreCase keeps the check culture-
        // independent while accepting `.YAML` etc. on case-sensitive
        // filesystems too.
        return trimmed.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve <paramref name="urlOrPath"/> to a document stream and hand it
    /// to the SDK reader. Throws on parse / IO errors so the caller can
    /// surface them as discovery errors rather than masking them.
    /// </summary>
    public async Task<IAsyncApiDocument> LoadAsync(string urlOrPath, CancellationToken ct)
    {
        var reader = _services.GetRequiredService<IAsyncApiDocumentReader>();

        // Treat anything with a scheme as a URL, everything else as a file
        // path. Mirrors the OpenAPI plugin's behaviour for OpenAPI doc
        // discovery — minimum coverage for the Phase A demo.
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var http = new HttpClient();
            await using var httpStream = await http.GetStreamAsync(uri, ct).ConfigureAwait(false);
            var fromUrl = await reader.ReadAsync(httpStream, ct).ConfigureAwait(false);
            return fromUrl ?? throw new InvalidOperationException(
                $"AsyncAPI reader returned no document for '{urlOrPath}'.");
        }

        await using var fileStream = File.OpenRead(urlOrPath);
        var fromFile = await reader.ReadAsync(fileStream, ct).ConfigureAwait(false);
        return fromFile ?? throw new InvalidOperationException(
            $"AsyncAPI reader returned no document for '{urlOrPath}'.");
    }
}

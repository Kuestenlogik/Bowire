// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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
    /// <remarks>
    /// Returns both the typed document and the pre-normalised raw YAML so
    /// the caller can hand the raw text to <see cref="AsyncApiBindingsExtractor"/>
    /// without re-fetching the URL.
    /// </remarks>
    public async Task<LoadResult> LoadAsync(string urlOrPath, CancellationToken ct)
    {
        var reader = _services.GetRequiredService<IAsyncApiDocumentReader>();
        var raw = await ReadRawAsync(urlOrPath, ct).ConfigureAwait(false);

        // YAML pre-normaliser: quote scalars on enum-typed properties that
        // the SDK reader otherwise mis-deserialises (asyncapi/net-sdk#76).
        // JSON files don't have the implicit-type problem, so the
        // normaliser is a no-op on them — running it unconditionally
        // keeps the code path uniform.
        var normalised = AsyncApiYamlPreNormaliser.Normalise(raw);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(normalised));
        var document = await reader.ReadAsync(stream, ct).ConfigureAwait(false);
        if (document is null)
        {
            throw new InvalidOperationException(
                $"AsyncAPI reader returned no document for '{urlOrPath}'.");
        }
        return new LoadResult(document, normalised);
    }

    /// <summary>
    /// Outcome of <see cref="LoadAsync"/>: the typed AsyncAPI model from
    /// the SDK reader plus the same pre-normalised YAML text the reader
    /// consumed, so secondary walkers (binding-detail extractor,
    /// pre-flight validators) don't need to re-fetch the URL.
    /// </summary>
    public sealed record LoadResult(IAsyncApiDocument Document, string NormalisedYaml);

    /// <summary>
    /// Reads the document content as a string so the pre-normaliser can
    /// scan it before the reader. The split between read + parse adds one
    /// in-memory copy of the document, which for AsyncAPI files (typically
    /// well under 1 MB) is negligible.
    /// </summary>
    /// <remarks>
    /// HTTP reads go through <see cref="HttpClient.GetByteArrayAsync(Uri, CancellationToken)"/>
    /// rather than <c>GetStringAsync</c> because the latter's encoding
    /// heuristic only trusts the <c>Content-Type charset=</c> attribute for
    /// MIME types it considers textual, and <c>application/yaml</c> isn't
    /// on that list. The result is a silent latin-1 decode of perfectly
    /// fine UTF-8 bytes — em-dashes turn into <c>â€"</c>. Reading the raw
    /// bytes and decoding as UTF-8 explicitly avoids that gotcha. BOM is
    /// preserved by the byte read and stripped by the UTF-8 decoder.
    /// </remarks>
    private static async Task<string> ReadRawAsync(string urlOrPath, CancellationToken ct)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        return await File.ReadAllTextAsync(urlOrPath, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
    }
}

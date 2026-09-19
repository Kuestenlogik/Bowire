// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// Automatic Persisted Queries — the hash, and reading the server's
/// "I don't know that one" (#713).
/// </summary>
/// <remarks>
/// <para>
/// APQ exists to stop sending the same document over and over. The client
/// sends only a SHA-256 of it; a server that has seen the document before
/// runs it from its own store. The saving is the whole document on every
/// request after the first, which on a mobile link or behind a per-request
/// body limit is the difference between workable and not.
/// </para>
/// <para>
/// It only works as a pair. A client that sends the hash and gives up on
/// <c>PERSISTED_QUERY_NOT_FOUND</c> fails every first call to every server
/// it has not primed. That is why this ships with the retry, not before
/// it: half of APQ is worse than none, because the failure looks like the
/// server being broken.
/// </para>
/// <para>
/// <b>Why this is hand-written when a library does it.</b> graphql-dotnet's
/// GraphQL.Client has exactly this, under
/// <c>GraphQLHttpClientOptions.EnableAutomaticPersistedQueries</c> and
/// <c>DisableAPQ</c> — the same two knobs, arrived at independently, which
/// is a good sign about the shape. It was evaluated and not adopted, for
/// one reason: that client has no way to send a query over GET, and Bowire
/// does (#713). Adopting it would not replace this plugin's send path, it
/// would stand a second HTTP client beside it, with a second copy of the
/// certificate-trust configuration to keep in step. For a spec surface
/// this small — one hash, two error codes, version 1, stable for years —
/// the dependency costs more than it saves.
/// </para>
/// <para>
/// That reasoning does not extend to the subscription transports. Those
/// are hand-written for a different reason: they route through Bowire's own
/// WebSocket plugin so the workbench's rail can see the traffic and the
/// mock can replay it. A library socket would make GraphQL subscriptions
/// invisible to Bowire's own tooling.
/// </para>
/// </remarks>
internal static class GraphQLPersistedQuery
{
    /// <summary>
    /// What the server says when it has never seen this hash. Defined by
    /// the APQ convention rather than by any one implementation, which is
    /// why it is matched as a string and not mapped to a status code.
    /// </summary>
    public const string NotFoundCode = "PERSISTED_QUERY_NOT_FOUND";

    /// <summary>
    /// What a server says when it will not do APQ at all — for instance
    /// because it only serves a fixed, pre-registered allow-list.
    /// </summary>
    public const string NotSupportedCode = "PERSISTED_QUERY_NOT_SUPPORTED";

    /// <summary>
    /// The SHA-256 of a document, lower-case hex, as the APQ extension
    /// expects it.
    /// </summary>
    /// <remarks>
    /// Over the document exactly as it will be sent. Not trimmed, not
    /// normalised: the server hashes the bytes it stored, so any tidying
    /// here would produce a hash that never matches and an APQ round trip
    /// that always costs two requests instead of one.
    /// </remarks>
    public static string Hash(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
    }

    /// <summary>
    /// The <c>extensions</c> object that carries a persisted-query hash.
    /// </summary>
    public static object Extension(string hash) => new
    {
        persistedQuery = new { version = 1, sha256Hash = hash },
    };

    /// <summary>
    /// Whether a response is the server asking for the full document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both codes mean "send the document": one because this server has
    /// never seen it, the other because this server does not do APQ. The
    /// caller's next move is the same either way, so they are not
    /// distinguished here — only <see cref="IsPermanentlyUnsupported"/>
    /// cares, and only so a second call does not repeat a hash the server
    /// has already said it will not use.
    /// </para>
    /// <para>
    /// Read out of the <c>errors</c> array rather than the HTTP status: an
    /// APQ miss is a 200 with a GraphQL error, because as far as HTTP is
    /// concerned nothing went wrong.
    /// </para>
    /// </remarks>
    public static bool IsMiss(JsonElement response) => MissCode(response) is not null;

    /// <summary>
    /// Whether the server said it does not support APQ at all, as opposed
    /// to not knowing this particular document.
    /// </summary>
    public static bool IsPermanentlyUnsupported(JsonElement response)
        => string.Equals(MissCode(response), NotSupportedCode, StringComparison.Ordinal);

    private static string? MissCode(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;
        if (!response.TryGetProperty("errors", out var errors)) return null;
        if (errors.ValueKind != JsonValueKind.Array) return null;

        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object) continue;

            // The conventional place. Some servers put it here…
            if (error.TryGetProperty("extensions", out var ext)
                && ext.ValueKind == JsonValueKind.Object
                && ext.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && Recognised(code.GetString()) is { } fromExtensions)
            {
                return fromExtensions;
            }

            // …and some put it in the message, which the APQ write-up
            // itself does in its examples. Both are in the wild, and a
            // client that reads only one of them silently loses APQ
            // against half the servers that offer it.
            if (error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && Recognised(message.GetString()) is { } fromMessage)
            {
                return fromMessage;
            }
        }
        return null;
    }

    private static string? Recognised(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains(NotSupportedCode, StringComparison.Ordinal)) return NotSupportedCode;
        if (text.Contains(NotFoundCode, StringComparison.Ordinal)) return NotFoundCode;
        return null;
    }
}

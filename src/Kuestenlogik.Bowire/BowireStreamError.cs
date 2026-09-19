// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Why a server-streaming call stopped (#712).
/// </summary>
/// <remarks>
/// <para>
/// The streaming contract had no way to say this. A frame is a JSON string
/// (or <see cref="StreamFrame"/> for plugins that expose wire bytes), so a
/// plugin that needed to report "this stream is over, and here is why" put
/// the reason in the payload — at the one place the consumer expects
/// payload. Twelve sites did that in four independently invented shapes,
/// and the gRPC plugin had grown a private <c>ErrorCode</c> field on its
/// own internal frame record, which is the clearest sign the contract was
/// missing a concept rather than a convenience.
/// </para>
/// <para>
/// What it cost: <c>api.js</c> parsed every frame as data and then set
/// <c>status: 'OK'</c>. Nothing in the handler recognised an error frame,
/// so an aborted stream was shown as a message and logged as "Completed".
/// </para>
/// </remarks>
/// <param name="Kind">
/// A short, lower-case word for the class of failure. See
/// <see cref="BowireStreamErrorKinds"/> for the ones Bowire already uses.
/// </param>
/// <param name="Message">What to show the operator. Required.</param>
/// <param name="Code">
/// The wire protocol's own code when it has one — a gRPC status, an HTTP
/// status, a broker's reason code. Null when the protocol has none.
/// </param>
public sealed record StreamError(string Kind, string Message, string? Code = null);

/// <summary>
/// The <see cref="StreamError.Kind"/> values Bowire's own plugins use.
/// </summary>
/// <remarks>
/// <para>
/// Constants rather than an enum, and that is the deliberate part. An enum
/// in core would force every new plugin to press its failure into a word
/// somebody else chose — which is the same mistake the old state made, only
/// from the other direction: there, every plugin invented its own shape;
/// with a closed enum, none could. A plugin whose protocol has a kind not
/// listed here writes its own word and is not wrong.
/// </para>
/// <para>
/// The consumer treats an unknown kind as a failure like any other. The
/// kind is for reading and filtering, never for control flow — nothing
/// downstream may switch on it and fall through to "success" on a word it
/// does not know.
/// </para>
/// </remarks>
public static class BowireStreamErrorKinds
{
    /// <summary>The connection failed or dropped: DNS, TCP, TLS, a socket close.</summary>
    public const string Transport = "transport";

    /// <summary>The peer spoke the protocol wrongly, or refused the handshake.</summary>
    public const string Protocol = "protocol";

    /// <summary>The peer declined: auth, quota, a policy rule.</summary>
    public const string Refused = "refused";

    /// <summary>Nothing arrived within the time the caller was willing to wait.</summary>
    public const string Idle = "idle";

    /// <summary>The peer reported an error of its own and ended the stream.</summary>
    public const string Server = "server";

    /// <summary>A prerequisite is missing on this host — an unloaded plugin, an unregistered receiver.</summary>
    public const string NotConfigured = "not-configured";
}

/// <summary>
/// The envelope a JSON-only streaming plugin yields to end a stream with a
/// reason (#712).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IBowireProtocol.InvokeStreamAsync"/> yields strings, so a
/// plugin on that path has no typed slot to put an error in. It yields this
/// envelope as its last frame instead. Plugins that implement
/// <see cref="IBowireStreamingWithWireBytes"/> set
/// <see cref="StreamFrame.Error"/> directly and do not need it.
/// </para>
/// <para>
/// The key is deliberately unlovely. It has to be something no server's
/// payload would ever contain at its top level, because the alternative —
/// a bare <c>error</c> key — is exactly what several plugins already emit
/// as *data*, and a consumer cannot tell those apart from a stream that
/// actually ended.
/// </para>
/// </remarks>
public static class BowireStreamErrorEnvelope
{
    /// <summary>The reserved top-level key.</summary>
    public const string Key = "__bowireStreamError";

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The frame to yield as the last one on a stream that failed.</summary>
    public static string Frame(StreamError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return JsonSerializer.Serialize(
            new Dictionary<string, StreamError> { [Key] = error }, s_options);
    }

    /// <summary>
    /// Convenience for the common call: build the frame from its parts.
    /// </summary>
    public static string Frame(string kind, string message, string? code = null)
        => Frame(new StreamError(kind, message, code));

    /// <summary>
    /// The error a frame carries, or null when it is ordinary data.
    /// </summary>
    /// <remarks>
    /// Anything that is not a JSON object with the reserved key is data.
    /// That includes malformed JSON: a plugin is free to stream whatever a
    /// server sent, and deciding that unparseable output means "error"
    /// would put words in the server's mouth.
    /// </remarks>
    public static StreamError? TryRead(string? frameJson)
    {
        if (string.IsNullOrWhiteSpace(frameJson)) return null;
        if (!frameJson.Contains(Key, StringComparison.Ordinal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(frameJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(Key, out var node)) return null;
            if (node.ValueKind != JsonValueKind.Object) return null;

            var kind = node.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var message = node.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = node.TryGetProperty("code", out var c) ? c.GetString() : null;

            // A message is what makes the error usable. Without one this is
            // not a stream error anybody can act on, so treat it as data
            // rather than inventing wording.
            if (string.IsNullOrWhiteSpace(message)) return null;

            return new StreamError(
                string.IsNullOrWhiteSpace(kind) ? BowireStreamErrorKinds.Transport : kind,
                message,
                string.IsNullOrWhiteSpace(code) ? null : code);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

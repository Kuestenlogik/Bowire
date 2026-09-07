// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Parses Bowire's optional <c>hint@url</c> form for server URLs. A
/// plugin hint tells the discovery + invoke endpoints which protocol
/// plugin to consult — without one Bowire still probes every plugin
/// in turn, which is fine in practice but slow when one of them needs
/// a long network round-trip to discover that the URL isn't theirs
/// (e.g. the gRPC plugin opens an HTTP/2 channel against an HTTP/1.1
/// GraphQL server and waits for the handshake to time out).
/// <para>
/// Examples:
/// </para>
/// <list type="bullet">
///   <item><c>grpc@https://api.example.com:443</c> → hint=grpc, url=https://api.example.com:443</item>
///   <item><c>signalr@https://api.example.com/hubs/chat</c> → hint=signalr, url=https://api.example.com/hubs/chat</item>
///   <item><c>https://alice:pwd@host.com</c> → no hint, url stays intact (the <c>@</c> is URI userinfo)</item>
///   <item><c>https://api.example.com</c> → no hint, url stays intact</item>
///   <item><c>udp://239.0.13.37:8137</c> → no hint, the URL scheme itself already routes the plugin</item>
/// </list>
/// </summary>
public static class BowireServerUrl
{
    /// <summary>
    /// Split a raw <paramref name="raw"/> server URL into an optional
    /// plugin hint and the bare URL. Falls back to <c>(null, raw)</c>
    /// when the input doesn't match the hint pattern, so callers can
    /// pass the result straight through their existing URL plumbing.
    /// </summary>
    /// <remarks>
    /// Three rules together keep the parse unambiguous against URI
    /// userinfo (<c>user:pass@host</c>), bare email-style strings,
    /// and arbitrary plugin scheme URLs:
    /// <list type="number">
    ///   <item>
    ///     The hint token (everything before the first <c>@</c>) must
    ///     contain only ASCII letters, digits, or hyphens. Anything
    ///     with <c>:</c> or <c>/</c> in it is URI userinfo.
    ///   </item>
    ///   <item>
    ///     The remainder after the <c>@</c> must contain <c>://</c>
    ///     (i.e. look like a URL with a scheme). This rejects bare
    ///     <c>alice@example.com</c> — that's not a URL at all and
    ///     should fall through to the plugin's own URL parser.
    ///   </item>
    ///   <item>
    ///     The hint is otherwise opaque: validation that the hint
    ///     names an actually-loaded plugin happens at the call site,
    ///     not here, so adding a new plugin doesn't have to update
    ///     this helper.
    ///   </item>
    /// </list>
    /// </remarks>
    public static (string? PluginHint, string Url) Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, raw ?? string.Empty);

        var atIdx = raw.IndexOf('@', StringComparison.Ordinal);
        if (atIdx <= 0) return (null, raw);

        var head = raw[..atIdx];
        var rest = raw[(atIdx + 1)..];

        // Userinfo would carry a colon (user:pass@host) or arrive as
        // part of a full scheme (https://user@host). Either way, head
        // contains characters the hint pattern doesn't allow.
        if (head.Contains(':') || head.Contains('/')) return (null, raw);

        // Hint chars: a-z, A-Z, 0-9, '-'. Plugin ids are conventionally
        // lowercase but we don't enforce case here — case-insensitive
        // matching happens at the call site.
        for (var i = 0; i < head.Length; i++)
        {
            var c = head[i];
            if (!char.IsLetterOrDigit(c) && c != '-') return (null, raw);
        }

        // The rest must look like a URL with a scheme. 'alice@example.com'
        // (no '://' anywhere) is an email-style string, not a hinted URL,
        // so we leave it for the plugin's own URL parser to deal with.
        if (!rest.Contains("://", StringComparison.Ordinal)) return (null, raw);

        return (head, rest);
    }

    /// <summary>
    /// Convenience accessor that returns just the bare URL — equivalent
    /// to <c>Parse(raw).Url</c>. Useful where the call site needs only
    /// the URL and doesn't care whether a hint was present.
    /// </summary>
    public static string StripHint(string? raw) => Parse(raw).Url;
}

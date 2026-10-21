// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// A parsed <c>{{keyring.service/account}}</c> reference: a mandatory
/// <see cref="Service"/> and an optional <see cref="Account"/>. A bare
/// <c>{{keyring.service}}</c> leaves <see cref="Account"/> null, which each
/// backend maps to "any account under this service".
/// </summary>
/// <remarks>
/// <para>
/// A plain reference is split on the <em>first</em> slash, so the account
/// may contain slashes: <c>github.com/deploy-bot</c> is service
/// <c>github.com</c>, account <c>deploy-bot</c>; <c>vault/team/bot</c> is
/// service <c>vault</c>, account <c>team/bot</c>.
/// </para>
/// <para>
/// A reference that starts with a URL scheme (<c>&lt;scheme&gt;://</c>)
/// names a URL service and is split on the <em>last</em> slash after the
/// host, so the service keeps its path (#870):
/// <c>https://api.example.com/v1/deploy-bot</c> is service
/// <c>https://api.example.com/v1</c>, account <c>deploy-bot</c>;
/// <c>https://api.example.com/deploy-bot</c> is service
/// <c>https://api.example.com</c>, account <c>deploy-bot</c>; a host-only
/// URL (with or without a trailing slash) is a service without an account.
/// </para>
/// <para>
/// <c>%2F</c> (any case) is an escaped slash in either part and is decoded
/// after the split, so any edge case can be written — e.g. an account with
/// a slash under a URL service: <c>https://api.example.com/v1/team%2Fbot</c>.
/// <c>%25</c> decodes to <c>%</c> so a literal <c>%2F</c> is written
/// <c>%252F</c>. No other escape is decoded: the reference is not a URL to
/// percent-decode, only these two are needed to write every split.
/// </para>
/// </remarks>
public readonly record struct KeyringReference(string Service, string? Account)
{
    /// <summary>
    /// Parse the portion of a placeholder key that follows the
    /// <c>keyring.</c> prefix. Returns <c>false</c> for an empty or
    /// whitespace-only reference, a missing service, or a URL without a
    /// host, so the caller leaves the placeholder intact instead of issuing
    /// a meaningless lookup.
    /// </summary>
    public static bool TryParse(string reference, out KeyringReference parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        var trimmed = reference.Trim();
        int slash;
        var schemeEnd = SchemeLength(trimmed);
        if (schemeEnd > 0)
        {
            // URL service: the host runs up to the first slash after "://";
            // the account (if any) follows the last slash after the host.
            var pathStart = trimmed.IndexOf('/', schemeEnd);
            if (pathStart == schemeEnd) return false; // empty host
            slash = pathStart < 0 ? -1 : trimmed.LastIndexOf('/');
        }
        else
        {
            slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        }

        var service = (slash < 0 ? trimmed : trimmed[..slash]).Trim();
        var account = slash < 0 ? string.Empty : trimmed[(slash + 1)..].Trim();
        if (service.Length == 0) return false;
        parsed = new KeyringReference(
            Unescape(service),
            account.Length == 0 ? null : Unescape(account));
        return true;
    }

    /// <summary>
    /// Length of a leading <c>&lt;scheme&gt;://</c> (RFC 3986 scheme
    /// characters), or 0 when the reference does not start with one.
    /// </summary>
    private static int SchemeLength(string text)
    {
        if (text.Length == 0 || !char.IsAsciiLetter(text[0])) return 0;
        var i = 1;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '+' or '-' or '.')) i++;
        return string.CompareOrdinal(text, i, "://", 0, 3) == 0 ? i + 3 : 0;
    }

    /// <summary>
    /// Decode <c>%2F</c> / <c>%2f</c> to <c>/</c> and <c>%25</c> to
    /// <c>%</c> in one left-to-right pass (so <c>%252F</c> yields a literal
    /// <c>%2F</c>); every other <c>%</c> sequence stays as written.
    /// </summary>
    private static string Unescape(string part)
    {
        if (!part.Contains('%', StringComparison.Ordinal)) return part;
        var sb = new StringBuilder(part.Length);
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] == '%' && i + 2 < part.Length)
            {
                if (part[i + 1] == '2' && part[i + 2] is 'F' or 'f')
                {
                    sb.Append('/');
                    i += 2;
                    continue;
                }
                if (part[i + 1] == '2' && part[i + 2] == '5')
                {
                    sb.Append('%');
                    i += 2;
                    continue;
                }
            }
            sb.Append(part[i]);
        }
        return sb.ToString();
    }
}

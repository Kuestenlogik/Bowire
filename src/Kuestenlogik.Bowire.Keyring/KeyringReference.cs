// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// A parsed <c>{{keyring.service/account}}</c> reference. The text after
/// the <c>keyring.</c> prefix is split on the <em>first</em> slash into a
/// mandatory <see cref="Service"/> and an optional <see cref="Account"/>;
/// a bare <c>{{keyring.service}}</c> leaves <see cref="Account"/> null,
/// which each backend maps to "any account under this service".
/// </summary>
/// <remarks>
/// Splitting on the first slash (rather than the last) keeps the common
/// <c>service/account</c> shape unambiguous while still allowing a service
/// name that itself contains slashes — e.g.
/// <c>{{keyring.https://api.example.com/deploy-bot}}</c> parses to service
/// <c>https://api.example.com</c>, account <c>deploy-bot</c>. Callers that
/// need a slash in the account can percent-encode it; the store lookups
/// treat the decoded strings opaquely.
/// </remarks>
public readonly record struct KeyringReference(string Service, string? Account)
{
    /// <summary>
    /// Parse the portion of a placeholder key that follows the
    /// <c>keyring.</c> prefix. Returns <c>false</c> for an empty or
    /// whitespace-only reference so the caller leaves the placeholder
    /// intact instead of issuing a meaningless empty-service lookup.
    /// </summary>
    public static bool TryParse(string reference, out KeyringReference parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        var trimmed = reference.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            parsed = new KeyringReference(trimmed, null);
            return true;
        }

        var service = trimmed[..slash].Trim();
        var account = trimmed[(slash + 1)..].Trim();
        if (service.Length == 0) return false;
        parsed = new KeyringReference(service, account.Length == 0 ? null : account);
        return true;
    }
}

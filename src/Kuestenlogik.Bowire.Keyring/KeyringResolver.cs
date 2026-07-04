// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// The single resolution entry point shared by the workbench endpoint
/// (<c>POST /api/vars/keyring</c>) and the CLI's flow variable resolver.
/// Applies the <see cref="KeyringOptions.Enabled"/> gate, parses the
/// reference, and delegates the store read to the injected
/// <see cref="IKeyringBackend"/>.
/// </summary>
public sealed class KeyringResolver
{
    private readonly KeyringOptions _options;
    private readonly IKeyringBackend _backend;

    /// <summary>Create a resolver over the given options + backend.</summary>
    public KeyringResolver(KeyringOptions options, IKeyringBackend backend)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>Whether the store read is currently permitted.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>The active backend id (see <see cref="IKeyringBackend.BackendId"/>).</summary>
    public string BackendId => _backend.BackendId;

    /// <summary>
    /// Resolve one reference (the text after the <c>keyring.</c> prefix).
    /// Never throws — a disabled resolver, an unparseable reference, a
    /// miss, or a backend error each map to a distinct
    /// <see cref="KeyringReadStatus"/> so callers can leave the placeholder
    /// intact and, where useful, tell the operator why.
    /// </summary>
    public KeyringReadResult Resolve(string reference)
    {
        if (!_options.Enabled) return KeyringReadResult.Failed("keyring disabled");
        if (!KeyringReference.TryParse(reference, out var parsed))
        {
            return KeyringReadResult.Failed("invalid keyring reference");
        }
        return _backend.Read(parsed);
    }
}

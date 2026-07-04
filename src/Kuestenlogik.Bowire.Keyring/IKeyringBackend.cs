// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// The platform-specific credential-store read seam. The one shipped
/// implementation is <see cref="OsKeyringBackend"/> (Windows Credential
/// Manager / macOS Keychain / libsecret); tests inject an in-memory fake
/// so the resolver, endpoint, and CLI paths can be exercised without
/// touching a real store.
/// </summary>
public interface IKeyringBackend
{
    /// <summary>
    /// The backend id actually in effect (<c>wincred</c>, <c>keychain</c>,
    /// <c>secret-tool</c>, or <c>none</c> when no store is available on the
    /// running OS). Surfaced by the status endpoint so the UI can explain
    /// why a lookup returned nothing.
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Read one secret. Returns a <see cref="KeyringReadResult"/> that
    /// distinguishes "found" from "not found" from "the store errored",
    /// so the caller can leave the placeholder intact on a miss without
    /// masking a genuine backend failure in the logs.
    /// </summary>
    KeyringReadResult Read(KeyringReference reference);
}

/// <summary>Outcome of a single <see cref="IKeyringBackend.Read"/>.</summary>
public readonly record struct KeyringReadResult(
    KeyringReadStatus Status, string? Value, string? Error)
{
    /// <summary>The secret was found.</summary>
    public static KeyringReadResult Found(string value) => new(KeyringReadStatus.Found, value, null);
    /// <summary>No entry matched the reference.</summary>
    public static KeyringReadResult NotFound() => new(KeyringReadStatus.NotFound, null, null);
    /// <summary>The store errored (tool missing, access denied, locked).</summary>
    public static KeyringReadResult Failed(string error) => new(KeyringReadStatus.Error, null, error);
}

/// <summary>Discriminates the three <see cref="KeyringReadResult"/> cases.</summary>
public enum KeyringReadStatus
{
    /// <summary>No entry matched — a clean miss.</summary>
    NotFound = 0,
    /// <summary>The secret was resolved.</summary>
    Found = 1,
    /// <summary>The backend itself failed.</summary>
    Error = 2,
}

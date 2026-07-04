// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// Configuration knobs for the optional <c>Kuestenlogik.Bowire.Keyring</c>
/// package (#208 Phase 5). Bound from the <c>Bowire:Keyring</c>
/// configuration section; the CLI's <c>--keyring</c> / <c>--no-keyring</c>
/// flags feed the same <see cref="Enabled"/> key via the in-memory
/// configuration overlay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in by construction, inert until referenced.</b> The package
/// only exists if the host added <c>Kuestenlogik.Bowire.Keyring</c> as a
/// PackageReference (the standalone CLI bundles it; embedded hosts pick it
/// up explicitly). Even when present and <see cref="Enabled"/>, the OS
/// credential store is only ever read when a template literally references
/// a <c>{{keyring.service/account}}</c> placeholder — the resolver never
/// enumerates or scans the store. That per-use trigger is the effective
/// opt-in, the same shape as the AI package's <c>ai.*</c> vars.
/// </para>
/// <para>
/// <b>Local-first, zero egress.</b> The resolver only ever talks to the
/// local platform credential store (Windows Credential Manager, macOS
/// Keychain, libsecret). Nothing leaves the machine; there is no
/// Küstenlogik-hosted broker in this path. Resolved values are scrubbed
/// to <c>***</c> at every export boundary (workspace save, collection
/// share, HAR/curl export) exactly like <c>{{secret.*}}</c> vars.
/// </para>
/// </remarks>
public sealed class KeyringOptions
{
    /// <summary>
    /// Master switch. When <c>false</c>, the <c>/api/vars/keyring</c>
    /// endpoint short-circuits to an empty, <c>enabled:false</c> response
    /// and the CLI resolver leaves <c>{{keyring.*}}</c> placeholders
    /// intact, so an operator on a locked-down machine can hard-disable
    /// the store read without uninstalling the package. Defaults
    /// <c>true</c>: the read is already gated behind an explicit
    /// per-reference trigger, so the useful default is "available".
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Override the backend selection. <c>"auto"</c> (default) picks the
    /// native store for the running OS — <c>wincred</c> on Windows,
    /// <c>keychain</c> on macOS, <c>secret-tool</c> on Linux. An explicit
    /// value forces one backend, which is mainly useful for tests and for
    /// Linux hosts that expose a non-default libsecret collection. Unknown
    /// values fall back to the auto pick. Compared case-insensitively.
    /// </summary>
    public string Backend { get; set; } = "auto";
}

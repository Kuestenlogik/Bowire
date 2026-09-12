// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// A plugin whose <see cref="IBowireProtocol.Settings"/> are not knowable
/// synchronously (#693).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IBowireProtocol.Settings"/> is a property, which suits a .NET
/// plugin: it returns a list it already holds. A sidecar plugin does not
/// hold one. Its settings live in the subprocess and arrive in the
/// <c>initialize</c> reply, and the subprocess is spawned lazily — so
/// before anything has called it, the property has nothing truthful to
/// return.
/// </para>
/// <para>
/// This is the asynchronous half. A caller that is about to read
/// <see cref="IBowireProtocol.Settings"/> and wants the real answer calls
/// <see cref="PrepareSettingsAsync"/> first; afterwards the property
/// returns what the plugin reported. A plugin that does not implement this
/// interface needs no preparation, which is every .NET plugin.
/// </para>
/// <para>
/// It is a separate interface rather than a member on
/// <see cref="IBowireProtocol"/> on purpose: adding it there would change
/// the contract every protocol plugin implements, in every repository, to
/// carry something only sidecars need. That is the same argument
/// <see cref="IBowirePluginSettings"/> makes for pulling values rather than
/// pushing them.
/// </para>
/// </remarks>
public interface IBowireDeferredSettings
{
    /// <summary>
    /// Do whatever is needed to make <see cref="IBowireProtocol.Settings"/>
    /// answer truthfully — for a sidecar, spawn the process and complete the
    /// <c>initialize</c> handshake.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw: a plugin that cannot be prepared
    /// reports no settings, which is the same thing it reported before. The
    /// Settings dialog lists every other plugin either way.
    /// </remarks>
    Task PrepareSettingsAsync(CancellationToken ct = default);
}

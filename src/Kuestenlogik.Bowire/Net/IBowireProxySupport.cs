// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Net;

/// <summary>How far a protocol plugin's connections follow the proxy (#680).</summary>
public enum BowireProxySupport
{
    /// <summary>The plugin has not said. It may connect directly.</summary>
    Unknown,

    /// <summary>Every connection goes through the configured proxy.</summary>
    Full,

    /// <summary>Some connections do, others go direct; <see cref="IBowireProxySupport.ProxyNote"/> says which.</summary>
    Partial,

    /// <summary>Connections always go direct: the transport cannot use an HTTP proxy.</summary>
    None,

    /// <summary>The plugin makes no outbound connections (a listener).</summary>
    NotApplicable,
}

/// <summary>
/// Implemented by a protocol plugin to say whether its connections honour the
/// proxy. The settings page lists it, and a call through a plugin that cannot
/// follow an active proxy is logged as going direct — the alternative is a
/// plugin that silently appears to work around the proxy.
/// </summary>
/// <remarks>
/// Opt-in, detected with <c>is</c>, like the other capability interfaces. A
/// plugin without it is reported as <see cref="BowireProxySupport.Unknown"/>.
/// </remarks>
public interface IBowireProxySupport
{
    /// <summary>How far this plugin's connections follow the proxy.</summary>
    BowireProxySupport ProxySupport { get; }

    /// <summary>One sentence on what goes direct and why, for Partial and None.</summary>
    string? ProxyNote => null;

    /// <summary>
    /// The catalogue key for <see cref="ProxyNote"/> (#691): the workbench shows
    /// the translation and falls back to the English note.
    /// </summary>
    string? ProxyNoteKey => null;
}

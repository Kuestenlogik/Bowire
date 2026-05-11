// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Optional companion interface to <see cref="IBowireProtocol"/>: a
/// protocol plugin that knows the shape of its own payloads in advance
/// (DIS PDU layouts, TacticalAPI situation objects, …) can pre-populate
/// annotations for those shapes by implementing this interface.
/// </summary>
/// <remarks>
/// <para>
/// Returned annotations enter the resolver at
/// <see cref="AnnotationSource.Plugin"/> priority — overriding the
/// auto-detector, overridden by the user. Returning an empty sequence
/// is the supported "this plugin has no opinion" default and is exactly
/// what protocol plugins that carry opaque user payloads (REST, GraphQL,
/// generic gRPC, …) should do.
/// </para>
/// <para>
/// The interface is intentionally orthogonal to <see cref="IBowireProtocol"/>
/// so existing plugins are not forced to think about semantics. A plugin
/// implements both interfaces on the same class when it wants to ship
/// hints; the host's plugin registry casts on demand.
/// </para>
/// </remarks>
public interface IBowireSchemaHints
{
    /// <summary>
    /// Annotations this plugin would like to contribute for the given
    /// <paramref name="serviceId"/> / <paramref name="methodId"/> pair.
    /// May return an empty sequence (nothing to contribute) and the
    /// resolver behaves as if the plugin had not opted in.
    /// </summary>
    /// <param name="serviceId">Service identifier the plugin returned from <c>DiscoverAsync</c>.</param>
    /// <param name="methodId">Method identifier within <paramref name="serviceId"/>.</param>
    IEnumerable<Annotation> GetSchemaHints(string serviceId, string methodId);
}

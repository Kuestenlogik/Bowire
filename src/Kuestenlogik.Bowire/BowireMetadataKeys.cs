// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Metadata keys that mean something to a protocol plugin instead of being
/// forwarded to the server.
/// </summary>
/// <remarks>
/// <para>
/// Per-call metadata is normally headers, but a few entries are configuration
/// for the plugin itself and are stripped before anything reaches the wire.
/// They live here because the two ends of that agreement cannot see each
/// other: a plugin is loaded at runtime and referenced by nobody, so the host
/// and the security scanner had no way to name a key except by repeating the
/// literal — which is what
/// <c>BowireDiscoveryEndpoints</c> was already doing with the transport
/// marker.
/// </para>
/// <para>
/// The double underscores are not decoration. They mark a key as belonging to
/// Bowire rather than to the caller, so a header genuinely named
/// <c>descriptors</c> cannot collide with one of these.
/// </para>
/// </remarks>
public static class BowireMetadataKeys
{
    /// <summary>
    /// A compiled gRPC descriptor set, for a server that does not answer
    /// Server Reflection (#653).
    /// </summary>
    /// <remarks>
    /// The value is either a path to a <c>.protoset</c> file or JSON with a
    /// <c>path</c> or <c>base64</c> property. Produced by
    /// <c>protoc --descriptor_set_out=api.protoset --include_imports</c>.
    /// </remarks>
    public const string GrpcDescriptorSet = "__bowireGrpcDescriptors__";

    /// <summary>
    /// gRPC transport selection (<c>web</c> for gRPC-Web).
    /// </summary>
    /// <remarks>
    /// Also accepted as a query parameter on the server URL, because discovery
    /// had no metadata bag when that path was written.
    /// </remarks>
    public const string GrpcTransport = "__bowireGrpcTransport";
}

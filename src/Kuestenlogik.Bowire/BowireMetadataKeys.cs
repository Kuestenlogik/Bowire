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

    /// <summary>
    /// The plugin the caller explicitly pinned with <c>hint@url</c>, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DiscoverAsync</c> has no "was I explicitly asked for?" parameter,
    /// and some plugins need one. A plugin that discovers from a bundled
    /// schema rather than from the wire cannot probe: it would answer for
    /// every URL it is offered, so on the hint-less fan-out it invents
    /// services that are not there. The same is true of an ad-hoc
    /// separate-target fallback, which must not fire just because some
    /// unrelated endpoint happens to answer the right content type.
    /// </para>
    /// <para>
    /// The hint itself cannot carry the answer: <see cref="BowireServerUrl.Parse"/>
    /// consumes the <c>hint@</c> prefix before the plugin is reached, so a
    /// plugin gating on that prefix is gating on something it can never see.
    /// The TacticalAPI plugin did exactly that and returned an empty list on
    /// every path, including the one its own sample depends on, while its
    /// unit test passed by calling <c>DiscoverAsync</c> with a prefix
    /// production never delivers.
    /// </para>
    /// <para>
    /// So the core stitches the resolved plugin id onto the URL as this
    /// marker whenever a hint was given. One marker for every plugin, rather
    /// than the per-plugin ones SSE and SignalR each grew: those had to be
    /// kept aligned by hand across two files, and the next plugin to need
    /// the same bit either invents a third or, as here, gets it wrong.
    /// </para>
    /// </remarks>
    public const string PluginHint = "__bowirePluginHint";
}

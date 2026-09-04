// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// Where a call gets the descriptors it needs to marshal a message (#653).
/// </summary>
/// <remarks>
/// <para>
/// Protobuf is a binary format: without the descriptors there is no way to turn
/// <c>{}</c> into bytes, so every gRPC invoke and every channel open needs them
/// before it can do anything. Until this existed the only source was
/// <see cref="GrpcReflectionClient"/>, which meant a server with reflection
/// disabled — the recommended production state, and the one Bowire's own
/// scanner recommends — could not be called at all. A <c>.proto</c> uploaded
/// for exactly that case reached the sidebar and never the wire: the methods
/// listed, and every one of them failed with
/// <c>No file descriptors for '&lt;service&gt;'</c>.
/// </para>
/// <para>
/// Two methods, because there are two questions a caller asks of a schema:
/// <em>what can I call</em> and <em>how do I marshal this one call</em>. The
/// interface started at the second alone — that was the whole surface invoke
/// and channel-open used — and grew the first when discovery turned out to
/// need the same independence from reflection: a scan against a hardened
/// server could not even enumerate the methods it was meant to test. Nothing
/// else about the reflection client — its channel, its caching, its disposal —
/// is part of the contract.
/// </para>
/// <para>
/// This is not the same question as *where the schema is stored*. A supplied
/// descriptor set is unusable here whether it came from a file path, an
/// upload, or a workspace — see #654 for that half.
/// </para>
/// </remarks>
internal interface IGrpcDescriptorSource : IDisposable
{
    /// <summary>
    /// The descriptor for <paramref name="serviceName"/> and everything it
    /// transitively depends on.
    /// </summary>
    /// <remarks>
    /// The closure matters: <c>GrpcInvoker.BuildFileDescriptors</c> resolves
    /// cross-references between the returned files, so a source that hands back
    /// one file without its imports produces a descriptor that cannot be built.
    /// </remarks>
    Task<List<FileDescriptorProto>> ResolveAllDescriptorsAsync(
        string serviceName, CancellationToken ct = default);

    /// <summary>
    /// Every service the source knows about, with its methods.
    /// </summary>
    /// <remarks>
    /// The reflection implementation asks the server and reports what it is
    /// willing to admit to; the descriptor-set implementation reports what the
    /// operator handed over. Those can disagree — a set may describe a method
    /// the deployment does not host — and that is the caller's problem to
    /// notice, not something to paper over here.
    /// </remarks>
    Task<List<BowireServiceInfo>> ListServicesAsync(CancellationToken ct = default);
}

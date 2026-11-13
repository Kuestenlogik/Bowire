// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// How an embedding host tells the gRPC plugin about its contract (#992).
/// </summary>
/// <remarks>
/// <para>
/// Embedded, Bowire runs inside the process whose gRPC services it is asked
/// to show. Their descriptors are already there: protoc-generated code
/// carries a <see cref="FileDescriptor"/> per <c>.proto</c>. The plugin reads
/// them from the gRPC services the host maps, so a host that only calls
/// <c>AddBowire()</c> and <c>MapBowire()</c> is discovered and called with
/// nothing set here and without a reflection endpoint.
/// </para>
/// <para>
/// These options cover what that cannot see: a service the host does not map
/// itself, a contract that lives in another process, and a host that wants
/// the reflection endpoint after all.
/// </para>
/// </remarks>
public sealed class BowireGrpcOptions
{
    /// <summary>
    /// Descriptors to discover and call through, in addition to those of the
    /// gRPC services the host maps. Add the generated one, e.g.
    /// <c>Greeter.Descriptor.File</c> or <c>GreeterReflection.Descriptor</c>;
    /// its imports come along.
    /// </summary>
    public IList<FileDescriptor> Descriptors { get; } = [];

    /// <summary>
    /// A compiled <c>FileDescriptorSet</c>
    /// (<c>protoc --descriptor_set_out --include_imports</c>) to discover and
    /// call through, for services that live in another process and have no
    /// generated code in this one.
    /// </summary>
    public ReadOnlyMemory<byte> DescriptorSet { get; set; }

    /// <summary>
    /// The same as <see cref="DescriptorSet"/>, read from a file when the
    /// plugin first needs it. A relative path is resolved against the content
    /// root.
    /// </summary>
    public string? DescriptorSetFile { get; set; }

    /// <summary>
    /// Whether the plugin reads the descriptors of the gRPC services the
    /// host maps. On by default; switch it off to show only what
    /// <see cref="Descriptors"/> and the descriptor set name.
    /// </summary>
    public bool UseMappedServices { get; set; } = true;

    /// <summary>
    /// Whether <c>MapBowire()</c> also maps the gRPC Server Reflection
    /// endpoints (<c>grpc.reflection.v1</c> and <c>v1alpha</c>). Off by
    /// default.
    /// </summary>
    /// <remarks>
    /// Bowire does not need them to show the host's own services. Mapping
    /// them publishes the whole contract to every client that can reach the
    /// port, and a host that classifies or audits its endpoints has two more
    /// to account for. Switch this on when other reflection clients
    /// (grpcurl, a standalone Bowire) should be able to ask this host.
    /// </remarks>
    public bool MapReflectionService { get; set; }
}

/// <summary>Registration of <see cref="BowireGrpcOptions"/>.</summary>
public static class BowireGrpcServiceCollectionExtensions
{
    /// <summary>
    /// Configure how the gRPC plugin of an embedded Bowire finds the host's
    /// contract. Optional: without it the plugin reads the descriptors of the
    /// gRPC services the host maps and maps no reflection endpoint.
    /// </summary>
    public static IServiceCollection AddBowireGrpc(
        this IServiceCollection services, Action<BowireGrpcOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return services;
    }
}

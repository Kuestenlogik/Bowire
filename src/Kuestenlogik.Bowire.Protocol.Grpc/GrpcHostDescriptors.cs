// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// The descriptors an embedding host already has in its process (#992).
/// </summary>
/// <remarks>
/// <para>
/// Embedded discovery used to map the host's reflection service and read the
/// host's own services back over the wire. That made every host that embeds
/// Bowire publish a reflection endpoint as a side effect, and made discovery
/// depend on a network round trip to itself. Neither was necessary: the
/// descriptors are compiled into the host.
/// </para>
/// <para>
/// Three places are read, in this order, a file name counted once: the
/// descriptors named in <see cref="BowireGrpcOptions.Descriptors"/>, the
/// host-supplied descriptor set, and the gRPC services the host maps. For a
/// mapped service the route carries <see cref="GrpcMethodMetadata"/> with the
/// implementing type; the generated base class it derives from names its
/// container through <see cref="BindServiceMethodAttribute"/>, and that
/// container has the <c>Descriptor</c>. A service without generated code
/// (code-first contracts) has none and is left to reflection.
/// </para>
/// </remarks>
internal sealed class GrpcHostDescriptors(IServiceProvider services)
{
    private static readonly ConcurrentDictionary<Type, FileDescriptor?> s_byServiceType = new();

    private readonly Lock _setLock = new();
    private IReadOnlyList<FileDescriptorProto>? _fromSet;

    /// <summary>The plugin's options, or the defaults when the host set none.</summary>
    public BowireGrpcOptions Options
        => services.GetService<IOptions<BowireGrpcOptions>>()?.Value ?? s_defaults;

    private static readonly BowireGrpcOptions s_defaults = new();

    /// <summary>
    /// Every file the host can describe right now.
    /// </summary>
    /// <remarks>
    /// Read on each call rather than once: a host may map its gRPC services
    /// after <c>MapBowire()</c>, and the endpoint list is the only place that
    /// knows. The per-type lookup behind it is cached, so a call costs a walk
    /// over the routes.
    /// </remarks>
    public IReadOnlyList<FileDescriptorProto> Files()
    {
        var options = Options;
        var files = new List<FileDescriptorProto>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in options.Descriptors) Add(descriptor, files, names);

        foreach (var file in FromSet(options))
        {
            if (names.Add(file.Name)) files.Add(file);
        }

        if (options.UseMappedServices)
        {
            foreach (var descriptor in MappedServiceFiles()) Add(descriptor, files, names);
        }

        return files;
    }

    /// <summary>
    /// Whether <paramref name="serverUrl"/> is one of the addresses this host
    /// itself listens on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For its own address the host's descriptors are the whole answer, and
    /// discovery asks nothing over the wire. That is more than an
    /// optimisation. An embedded workbench is usually served from an
    /// HTTP/1.1 port, and a reflection call to such a port neither succeeds
    /// nor fails promptly: it sits until the discovery ceiling cancels it,
    /// and the gRPC plugin's whole answer, the host's services included,
    /// is dropped with it.
    /// </para>
    /// <para>
    /// The port decides, with a host that is loopback, the bound host, or
    /// anything at all when the host bound a wildcard. A request that came
    /// through a proxy under another name and port does not match and is
    /// asked like any other server, which is right: that address is the
    /// proxy's.
    /// </para>
    /// </remarks>
    public bool IsOwnAddress(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var target)) return false;

        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return false;

        foreach (var address in addresses)
        {
            // Kestrel writes wildcards as `+` and `*`, which Uri refuses.
            var wildcard = address.Contains("://+", StringComparison.Ordinal)
                || address.Contains("://*", StringComparison.Ordinal);
            var parseable = wildcard
                ? address.Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
                    .Replace("://*", "://0.0.0.0", StringComparison.Ordinal)
                : address;
            if (!Uri.TryCreate(parseable, UriKind.Absolute, out var bound) || bound.Port != target.Port) continue;

            var any = wildcard
                || (System.Net.IPAddress.TryParse(bound.Host.Trim('[', ']'), out var ip)
                    && (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)));
            if (any || target.IsLoopback
                || string.Equals(bound.Host, target.Host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void Add(FileDescriptor descriptor, List<FileDescriptorProto> files, HashSet<string> names)
    {
        if (!names.Add(descriptor.Name)) return;
        // Imports first: the builder resolves references between the files it
        // is given, and a dependent before its dependency cannot be repaired.
        foreach (var dependency in descriptor.Dependencies) Add(dependency, files, names);
        files.Add(GrpcDescriptorSet.ParseFile(descriptor.SerializedData));
    }

    private IEnumerable<FileDescriptor> MappedServiceFiles()
    {
        var seen = new HashSet<Type>();
        foreach (var source in services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints)
            {
                if (endpoint.Metadata.GetMetadata<GrpcMethodMetadata>() is not { } grpc) continue;
                if (!seen.Add(grpc.ServiceType)) continue;
                if (s_byServiceType.GetOrAdd(grpc.ServiceType, FileOf) is { } file) yield return file;
            }
        }
    }

    /// <summary>
    /// The <c>.proto</c> file behind a service implementation, or null when
    /// the service was not generated from one.
    /// </summary>
    private static FileDescriptor? FileOf(Type serviceType)
    {
        for (var type = serviceType; type is not null; type = type.BaseType)
        {
            if (type.GetCustomAttribute<BindServiceMethodAttribute>(inherit: false) is not { } bind) continue;

            var descriptor = bind.BindType
                .GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as Google.Protobuf.Reflection.ServiceDescriptor;
            if (descriptor is not null) return descriptor.File;
        }

        return null;
    }

    private IReadOnlyList<FileDescriptorProto> FromSet(BowireGrpcOptions options)
    {
        lock (_setLock)
        {
            if (_fromSet is not null) return _fromSet;

            var files = new List<FileDescriptorProto>();
            if (!options.DescriptorSet.IsEmpty)
            {
                try
                {
                    files.AddRange(GrpcDescriptorSet.Parse(options.DescriptorSet.Span));
                }
                catch (FormatException ex)
                {
                    throw new InvalidOperationException($"BowireGrpcOptions.DescriptorSet: {ex.Message}", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(options.DescriptorSetFile))
            {
                var root = services.GetService<IWebHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
                var path = Path.GetFullPath(options.DescriptorSetFile, root);
                // A configured file that is missing or unreadable is a
                // configuration error of the host, and it says so: discovery
                // quietly showing less would send the operator looking at the
                // server instead of at the path.
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"BowireGrpcOptions.DescriptorSetFile '{path}' could not be read: {ex.Message}", ex);
                }

                try
                {
                    files.AddRange(GrpcDescriptorSet.Parse(bytes));
                }
                catch (FormatException ex)
                {
                    throw new InvalidOperationException(
                        $"BowireGrpcOptions.DescriptorSetFile '{path}': {ex.Message}", ex);
                }
            }

            _fromSet = files;
            return files;
        }
    }
}

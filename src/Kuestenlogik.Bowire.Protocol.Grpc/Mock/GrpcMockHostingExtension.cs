// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;
using Grpc.Reflection;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Mock;

/// <summary>
/// gRPC plugin's hosting extension. Three responsibilities the mock
/// server delegates here when the loaded recording contains gRPC
/// steps:
/// <list type="number">
///   <item>Force HTTP/2 hosting (gRPC requires it; plaintext HTTP/1.1
///         and HTTP/2 can't share a port without TLS+ALPN).</item>
///   <item>Build a per-recording <c>ReflectionServiceImpl</c> from the
///         captured FileDescriptorSets and register it in DI.</item>
///   <item>Map the gRPC reflection endpoint so a peer Bowire
///         workbench can auto-discover the mocked services without
///         the user supplying <c>.proto</c> files out of band.</item>
/// </list>
/// </summary>
public sealed class GrpcMockHostingExtension : IBowireMockHostingExtension
{
    private List<global::Google.Protobuf.Reflection.ServiceDescriptor>? _serviceDescriptors;

    /// <inheritdoc/>
    public string Id => "grpc";

    /// <inheritdoc/>
    /// <remarks>
    /// Only steps that can actually serve something over the gRPC wire
    /// count: unary wire bytes (<c>responseBinary</c>), streaming frames
    /// (<c>receivedMessages</c>), or a reflection-servable
    /// <c>schemaDescriptor</c>. A grpc step with none of those (hand-
    /// authored recording, pre-v2 capture) has nothing to serve, and
    /// forcing the whole port to plaintext HTTP/2 for it would break
    /// every HTTP/1.1 replay in a mixed recording (GraphQL / SSE / REST
    /// steps become unreachable for ordinary clients; see the protocol
    /// note in MockServer).
    /// </remarks>
    public bool RequiresHttp2(BowireRecording recording) =>
        recording.Steps.Any(s =>
            string.Equals(s.Protocol, "grpc", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(s.ResponseBinary)
                || s.ReceivedMessages is { Count: > 0 }
                || !string.IsNullOrEmpty(s.SchemaDescriptor)));

    /// <inheritdoc/>
    public void ConfigureServices(
        IServiceCollection services, BowireRecording recording, ILoggerFactory loggerFactory)
    {
        // Collect FileDescriptors across every gRPC step's
        // schemaDescriptor. When at least one is present, the mock
        // exposes gRPC Server Reflection so a peer Bowire workbench
        // can auto-discover the mocked services without the user
        // supplying .proto files out of band.
        var fileDescriptors = DescriptorPool.BuildFrom(recording);
        _serviceDescriptors = fileDescriptors.SelectMany(f => f.Services).ToList();

        if (_serviceDescriptors.Count == 0) return;

        services.AddGrpc();
        // Grpc.Reflection's ReflectionServiceImpl takes a list of
        // ServiceDescriptor objects directly — no dependency on
        // grpc-dotnet's service-discovery plumbing, which wouldn't
        // help us anyway since the mock has no code-generated gRPC
        // services to discover.
        services.AddSingleton(new ReflectionServiceImpl(_serviceDescriptors));
    }

    /// <inheritdoc/>
    public void MapEndpoints(IEndpointRouteBuilder endpoints, BowireRecording recording)
    {
        if (_serviceDescriptors is null || _serviceDescriptors.Count == 0) return;
        endpoints.MapGrpcService<ReflectionServiceImpl>();
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Plugin-contributed hosting extension for the Bowire mock server.
/// Lets a protocol plugin participate in the Kestrel pipeline setup
/// (force HTTP/2, register DI services, map endpoints) based on the
/// loaded recording — without baking protocol-specific knowledge into
/// <c>Kuestenlogik.Bowire.Mock</c>.
/// </summary>
/// <remarks>
/// <para>
/// The canonical use case is gRPC server-reflection: when the
/// recording contains gRPC steps with attached
/// <c>schemaDescriptor</c>s, the mock host must add gRPC services,
/// register a <c>ReflectionServiceImpl</c> built from the captured
/// FileDescriptorSets, and map a gRPC endpoint so a peer Bowire
/// workbench can auto-discover the mocked methods. All of that lives
/// on the <c>Protocol.Grpc</c> plugin since the <c>plugin-isation</c>
/// refactor; <c>Kuestenlogik.Bowire.Mock</c> just iterates the registered
/// extensions and calls the lifecycle hooks at the right moment.
/// </para>
/// <para>
/// Discovered via
/// <c>PluginManager.EnumeratePluginServices&lt;IBowireMockHostingExtension&gt;()</c>
/// at MockServer startup. Default-implemented members let a plugin
/// opt into only the hooks it needs.
/// </para>
/// </remarks>
public interface IBowireMockHostingExtension
{
    /// <summary>
    /// Stable id, lower-case, matching the protocol the extension
    /// belongs to (e.g. <c>"grpc"</c>). Surfaced in logs only — the
    /// host never dispatches by id, it always asks each extension
    /// in turn.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Return <c>true</c> when at least one step in the recording
    /// needs HTTP/2 hosting. The mock server picks the highest-required
    /// protocol level across all extensions before configuring Kestrel.
    /// gRPC says yes when the recording contains gRPC steps;
    /// REST/SignalR/SSE say no.
    /// </summary>
    bool RequiresHttp2(BowireRecording recording) => false;

    /// <summary>
    /// Register DI services the protocol-specific mock surface needs.
    /// Called during MockServer service-collection setup, before the
    /// host builds the application. gRPC uses this to add
    /// <c>AddGrpc()</c> + a per-recording <c>ReflectionServiceImpl</c>.
    /// </summary>
    void ConfigureServices(IServiceCollection services, BowireRecording recording, ILoggerFactory loggerFactory) { }

    /// <summary>
    /// Map protocol-specific endpoints. Called inside the mock host's
    /// <c>UseEndpoints</c> block, after the recording-replay middleware
    /// is mounted. gRPC uses this to map the reflection service so peer
    /// workbenches can auto-discover.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints, BowireRecording recording) { }
}

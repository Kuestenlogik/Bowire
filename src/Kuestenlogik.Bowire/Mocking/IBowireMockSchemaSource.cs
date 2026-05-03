// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Plugin-contributed schema-to-recording converter for the Bowire
/// mock server's <c>--schema</c> / <c>--grpc-schema</c> /
/// <c>--graphql-schema</c> family of CLI flags. Each plugin handles one
/// schema kind (OpenAPI on <c>Protocol.Rest</c>, protobuf
/// FileDescriptorSet on <c>Protocol.Grpc</c>, GraphQL SDL on
/// <c>Protocol.GraphQL</c>) and returns a synthesised
/// <see cref="BowireRecording"/> the standard replay pipeline can
/// serve from.
/// </summary>
/// <remarks>
/// Discovered via <c>PluginManager.EnumeratePluginServices&lt;IBowireMockSchemaSource&gt;()</c>
/// at MockServer startup; selection is by <see cref="Kind"/> matching
/// the configured schema-kind label. Kinds are stable lower-case ids:
/// <c>"openapi"</c>, <c>"protobuf"</c>, <c>"graphql"</c>. Custom
/// schemas can register their own ids without colliding.
/// </remarks>
public interface IBowireMockSchemaSource
{
    /// <summary>
    /// Schema kind id matching the CLI flag — <c>"openapi"</c> for
    /// <c>--schema</c>, <c>"protobuf"</c> for <c>--grpc-schema</c>,
    /// <c>"graphql"</c> for <c>--graphql-schema</c>.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Build a <see cref="BowireRecording"/> from the schema file at
    /// <paramref name="path"/>. Implementations may throw
    /// <see cref="InvalidDataException"/> for malformed schemas — the
    /// host catches and surfaces the message.
    /// <para>
    /// For schema kinds whose responses are dispatch-time-dependent
    /// (e.g. GraphQL — the response shape varies with the selection
    /// set), implementations may return an empty recording and rely on
    /// a companion middleware. See
    /// <see cref="IBowireMockLiveSchemaHandler"/> for that path.
    /// </para>
    /// </summary>
    Task<BowireRecording> BuildAsync(string path, CancellationToken ct);
}

/// <summary>
/// Optional companion to <see cref="IBowireMockSchemaSource"/> for
/// schema kinds whose responses can't be pre-baked into a recording
/// (GraphQL is the canonical case — every selection set yields a
/// different response shape, so the mock has to interpret each
/// request as it arrives).
/// </summary>
/// <remarks>
/// The mock server registers any matching live-handler as middleware
/// in front of the recording-replay middleware. If
/// <see cref="TryHandleAsync"/> returns <c>true</c>, the request is
/// considered handled and the rest of the pipeline is skipped.
/// </remarks>
public interface IBowireMockLiveSchemaHandler
{
    /// <summary>
    /// Schema kind id this handler is paired with — should match a
    /// registered <see cref="IBowireMockSchemaSource.Kind"/>.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Load schema state from <paramref name="path"/>. Called once at
    /// MockServer startup. Implementations should cache parsed schema
    /// data on the instance for use by <see cref="TryHandleAsync"/>.
    /// </summary>
    Task LoadAsync(string path, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct);

    /// <summary>
    /// Handle the incoming request if it matches this schema kind.
    /// Return <c>true</c> when handled (response written), <c>false</c>
    /// to fall through to downstream middleware.
    /// </summary>
    Task<bool> TryHandleAsync(Microsoft.AspNetCore.Http.HttpContext ctx, CancellationToken ct);
}

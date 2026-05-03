// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Mock;

/// <summary>
/// GraphQL plugin's contribution to the Bowire mock server's
/// schema-only flow. Differs from the OpenAPI / Protobuf sources
/// because GraphQL responses are selection-set-dependent — there's no
/// way to pre-bake them into a static <see cref="BowireRecording"/>.
/// Therefore <see cref="BuildAsync"/> returns an empty recording and
/// the actual request handling lives in the companion
/// <see cref="IBowireMockLiveSchemaHandler"/> implementation, which
/// the mock server registers as middleware in front of the
/// recording-replay pipeline.
/// </summary>
public sealed class GraphQlMockSchemaSource : IBowireMockSchemaSource, IBowireMockLiveSchemaHandler
{
    private GraphQlSchemaHandler? _handler;

    /// <inheritdoc/>
    public string Kind => "graphql";

    /// <inheritdoc/>
    public Task<BowireRecording> BuildAsync(string path, CancellationToken ct)
    {
        // GraphQL has no pre-baked recording — every request's
        // response shape depends on its selection set. The companion
        // live-handler does the real work.
        return Task.FromResult(new BowireRecording());
    }

    /// <inheritdoc/>
    public async Task LoadAsync(string path, ILogger logger, CancellationToken ct)
    {
        _handler = await GraphQlSchemaHandler.LoadAsync(path, logger, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<bool> TryHandleAsync(HttpContext ctx, CancellationToken ct)
    {
        if (_handler is null) return Task.FromResult(false);
        return _handler.TryHandleAsync(ctx, ct);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.SchemaDesigner;

/// <summary>
/// Discoverable endpoint-mount entry point for the Schema Designer rail
/// (#247). Picked up by Core's <c>BowireApiEndpoints</c> scan via the
/// <see cref="IBowireEndpointContribution"/> seam, so the graph endpoint
/// inherits the auth-gated route group and the host's base path without core
/// knowing this package exists.
/// </summary>
public sealed class BowireSchemaDesignerEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireSchemaGraphEndpoints(basePath);
    }
}

/// <summary>
/// What the workbench posts to have a graph built: the descriptor sets it
/// already holds from discovery.
/// </summary>
/// <param name="Descriptors">
/// Base64 <c>FileDescriptorSet</c> payloads, straight off
/// <c>BowireServiceInfo.schemaDescriptor</c>. The client sends what it has
/// rather than the server re-discovering, because discovery costs a live
/// probe of the operator's server — up to twelve seconds — and the answer is
/// already on the page.
/// </param>
/// <param name="IncludeWellKnown">
/// Include <c>google/protobuf/*</c> types. Absent means false: those types
/// are referenced from everywhere and would dominate the graph.
/// </param>
public sealed record SchemaGraphRequest(
    IReadOnlyList<string>? Descriptors,
    bool IncludeWellKnown = false);

/// <summary>
/// The Schema Designer rail's HTTP surface.
/// </summary>
public static class BowireSchemaDesignerEndpoints
{
    /// <summary>
    /// Map <c>POST {basePath}/api/schema/graph</c> — turn descriptor sets
    /// into the node / edge model the rail draws.
    /// </summary>
    /// <remarks>
    /// A POST, not a GET, because the input is the descriptor payload rather
    /// than an identifier, and it is far past what a query string holds. The
    /// handler is a pure function of its body: it reads no stored state and
    /// makes no outbound call, so it stays honest about outbound calls being
    /// opt-in.
    /// </remarks>
    public static IEndpointRouteBuilder MapBowireSchemaGraphEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/schema/graph", (SchemaGraphRequest? request) =>
        {
            var encoded = request?.Descriptors ?? [];
            var sets = new List<byte[]>(encoded.Count);
            foreach (var item in encoded)
            {
                // A descriptor that will not decode is skipped rather than
                // failing the request: one unreadable service should not
                // blank the graph for every other service on the page.
                if (!string.IsNullOrEmpty(item) && TryDecode(item, out var bytes))
                    sets.Add(bytes);
            }

            var graph = SchemaGraphBuilder.Build(sets, request?.IncludeWellKnown ?? false);
            // Results.Ok serialises with the host's web defaults (camelCase),
            // the same way the Contracts rail ships its matrix. The payload
            // spells its own property names anyway, so there is nothing for a
            // second serialiser configuration to decide.
            return Results.Ok(ToWirePayload(graph));
        })
        .ExcludeFromDescription();

        return endpoints;
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        bytes = [];
        var buffer = new byte[((base64.Length * 3) + 3) / 4];
        if (!Convert.TryFromBase64String(base64, buffer, out var written)) return false;
        bytes = buffer[..written];
        return true;
    }

    /// <summary>
    /// Project the graph onto its wire shape. Kinds travel as lowercase
    /// strings rather than as the enum's ordinal so the payload stays
    /// readable and a reordered enum cannot silently change what a client
    /// sees — the same reason the Contracts rail projects its matrix.
    /// </summary>
    internal static object ToWirePayload(SchemaGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return new
        {
            nodes = graph.Nodes.Select(static n => new
            {
                id = n.Id,
                name = n.Name,
                kind = WireKind(n.Kind),
                file = n.File,
                service = n.Service,
                fieldCount = n.FieldCount,
                usageCount = n.UsageCount,
                nested = n.IsNested,
                fields = n.Fields.Select(static f => new
                {
                    name = f.Name,
                    type = f.Type,
                    repeated = f.IsRepeated,
                    map = f.IsMap,
                }).ToList(),
            }).ToList(),
            edges = graph.Edges.Select(static e => new
            {
                from = e.From,
                to = e.To,
                kind = WireKind(e.Kind),
                label = e.Label,
                repeated = e.IsRepeated,
                map = e.IsMap,
            }).ToList(),
            files = graph.Files,
        };
    }

    private static string WireKind(SchemaNodeKind kind) => kind switch
    {
        SchemaNodeKind.Message => "message",
        SchemaNodeKind.Enum => "enum",
        SchemaNodeKind.Method => "method",
        _ => "message",
    };

    private static string WireKind(SchemaEdgeKind kind) => kind switch
    {
        SchemaEdgeKind.Field => "field",
        SchemaEdgeKind.Request => "request",
        SchemaEdgeKind.Response => "response",
        _ => "field",
    };
}

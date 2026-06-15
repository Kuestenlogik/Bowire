// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Protocol.Otlp;

/// <summary>
/// HTTP listener endpoints for the OTLP exporter spec
/// (<c>POST /v1/traces</c>, <c>/v1/metrics</c>, <c>/v1/logs</c>).
/// Sits behind a configurable <c>basePath</c> on the hosting
/// application so the workbench can mount the listener at
/// <c>/otlp</c> while the rest of the OTLP wire shape stays
/// spec-canonical underneath.
/// </summary>
public static class OtlpReceiverEndpoints
{
    /// <summary>
    /// Register the OTLP receiver endpoints. The handlers buffer each
    /// received export into the singleton <see cref="OtlpEnvelopeStore"/>
    /// and reply <c>200 OK</c> with an empty body — the spec allows the
    /// server to return either the canonical
    /// <c>ExportTraceServiceResponse</c> proto or an empty body for
    /// success.
    /// </summary>
    /// <param name="endpoints">The host's route builder.</param>
    /// <param name="basePath">
    /// Optional prefix mounted ahead of the canonical
    /// <c>/v1/{signal}</c> path. Leave empty to mount at the host
    /// root; use <c>"/otlp"</c> when the workbench shares the host
    /// with its own API.
    /// </param>
    public static IEndpointRouteBuilder MapBowireOtlpReceiver(
        this IEndpointRouteBuilder endpoints,
        string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        basePath ??= "";

        endpoints.MapPost($"{basePath}/v1/traces",
            (HttpContext ctx, OtlpEnvelopeStore store) => HandleAsync(ctx, store, OtlpSignalKind.Traces))
            .WithName("Bowire_Otlp_ReceiveTraces")
            .ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/v1/metrics",
            (HttpContext ctx, OtlpEnvelopeStore store) => HandleAsync(ctx, store, OtlpSignalKind.Metrics))
            .WithName("Bowire_Otlp_ReceiveMetrics")
            .ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/v1/logs",
            (HttpContext ctx, OtlpEnvelopeStore store) => HandleAsync(ctx, store, OtlpSignalKind.Logs))
            .WithName("Bowire_Otlp_ReceiveLogs")
            .ExcludeFromDescription();

        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(HttpContext ctx, OtlpEnvelopeStore store, OtlpSignalKind kind)
    {
        var contentType = ctx.Request.ContentType ?? "application/octet-stream";

        // Buffer the body so we can both surface it to the workbench
        // and keep the response cheap. OTLP exports cap out around a
        // few hundred KB even for chatty SUTs — buffering in memory
        // is acceptable for the passive-listener use case.
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
        var bytes = ms.ToArray();

        string? bodyJson = null;
        string? bodyBase64 = null;

        // JSON content type → keep the body as a UTF-8 string so the
        // workbench can render it without an intermediate decode hop.
        // Anything else (the OTLP default is application/x-protobuf,
        // but exporters also use application/octet-stream or gzipped
        // variants) is held as base64; Phase 2 swaps the base64
        // branch for an inline protobuf decode via vendored
        // opentelemetry-proto descriptors.
        if (IsJsonContentType(contentType))
        {
            bodyJson = bytes.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(bytes);
        }
        else
        {
            bodyBase64 = Convert.ToBase64String(bytes);
        }

        var envelope = new OtlpEnvelope(
            Kind:        kind,
            ReceivedAt:  DateTimeOffset.UtcNow,
            ContentType: contentType,
            BodyJson:    bodyJson,
            BodyBase64:  bodyBase64,
            BodyBytes:   bytes.LongLength,
            RemoteIp:    ctx.Connection.RemoteIpAddress?.ToString());

        store.Append(envelope);

        // OTLP/HTTP spec: 200 OK on success, empty body is fine. The
        // Content-Type header on the response matches the request so a
        // proto-only exporter doesn't choke on a JSON response shape.
        return Results.Bytes([], contentType);
    }

    internal static bool IsJsonContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        // First token before ';' — strips '; charset=utf-8' etc.
        var semi = contentType.IndexOf(';');
        var head = (semi >= 0 ? contentType[..semi] : contentType).Trim();
        return head.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || head.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// DI extension for the OTLP receiver — registers the singleton
/// <see cref="OtlpEnvelopeStore"/> so both the receiver Map endpoints
/// and the <see cref="BowireOtlpProtocol"/> discover the same buffer.
/// </summary>
public static class OtlpServiceCollectionExtensions
{
    public static IServiceCollection AddBowireOtlpReceiver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OtlpEnvelopeStore>();
        return services;
    }

    /// <summary>Test-only override — registers a specific store instance.</summary>
    internal static IServiceCollection AddBowireOtlpReceiver(this IServiceCollection services, OtlpEnvelopeStore store)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(store);
        return services;
    }
}

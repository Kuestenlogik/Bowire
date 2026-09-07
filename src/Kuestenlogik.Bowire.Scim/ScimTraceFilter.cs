// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>
/// Wraps every SCIM endpoint so a provisioning round-trip leaves evidence
/// (#639).
/// </summary>
/// <remarks>
/// <para>
/// Attached once, to the route group, when
/// <see cref="BowireScimOptions.TraceProvisioning"/> is on — so a route added
/// to <see cref="BowireScimEndpoints"/> later is traced without anyone having
/// to remember. A trace with a hole in it is worse than no trace, because it
/// still reads as evidence.
/// </para>
/// <para>
/// The body is read from the request only when there is one, and only for the
/// two things worth knowing about it: which PATCH dialect the connector used,
/// and which members Bowire does not model. The body is never written to the
/// file — a SCIM payload is somebody's name, e-mail and group membership, and
/// the questions #639 asks are answered by the shape, not the contents.
/// </para>
/// </remarks>
internal sealed class ScimTraceFilter(ScimProvisioningTrace trace) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;

        string? dialect = null;
        IReadOnlyCollection<string>? unmodelled = null;

        if (HasBody(http.Request))
        {
            // Buffered first: the handler downstream reads the same stream,
            // and reading it here without rewinding would hand it an empty
            // body — a trace that breaks provisioning is not a trace, it is
            // an outage with good intentions.
            var body = await ReadBodyAsync(http);
            if (body is not null)
            {
                if (HttpMethods.IsPatch(method)) dialect = ScimProvisioningTrace.DetectPatchDialect(body);
                unmodelled = ScimProvisioningTrace.UnmodelledAttributes(body);
            }
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next(context);
        }
        finally
        {
            // `finally`, so a handler that throws is still recorded. A 500
            // during a live round-trip is the single most interesting line
            // the file can contain, and it is the one a happy-path trace
            // would miss.
            trace.Record(
                method,
                http.Request.Path.Value ?? string.Empty,
                http.Request.QueryString.HasValue ? http.Request.QueryString.Value : null,
                http.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                dialect,
                unmodelled);
        }
    }

    private static bool HasBody(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method);

    /// <summary>
    /// Read the request body without consuming it, or <c>null</c> when it
    /// cannot be read.
    /// </summary>
    /// <remarks>
    /// Bounded deliberately. A connector's payload is one user or one group;
    /// anything larger is not a payload this needs to characterise, and
    /// buffering it in full would let a caller decide how much memory the
    /// trace costs.
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(HttpContext http)
    {
        const int maxBytes = 256 * 1024;

        try
        {
            http.Request.EnableBuffering();
            using var reader = new StreamReader(
                http.Request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);

            var buffer = new char[maxBytes];
            var read = await reader.ReadBlockAsync(buffer, http.RequestAborted);
            http.Request.Body.Position = 0;
            return read == 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            // Best-effort, like every other part of this: the connector is
            // waiting on the call, and losing a trace line is cheaper than
            // failing a sync that then retries forever.
            try { if (http.Request.Body.CanSeek) http.Request.Body.Position = 0; } catch { /* nothing else to do */ }
            return null;
        }
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability surface for protocol plugins that can dispatch a
/// generic <see cref="BowireMethodInfo"/> as an HTTP request — i.e. plugins
/// that know how to bucket fields by their <see cref="BowireFieldInfo.Source"/>
/// (path / query / header / body), substitute path placeholders, build a
/// request body, and parse the response.
///
/// The REST plugin implements this so that the gRPC plugin's HTTP-transcoding
/// discovery (which produces <see cref="BowireMethodInfo"/> instances with
/// <see cref="BowireMethodInfo.HttpMethod"/> + <see cref="BowireMethodInfo.HttpPath"/>
/// set) can be invoked over HTTP without core taking on any HTTP-specific
/// dependencies. If a Bowire build doesn't include the REST plugin,
/// <see cref="BowireProtocolRegistry.FindHttpInvoker"/> returns null and the
/// /api/invoke endpoint refuses HTTP transcoding requests with a clear error.
/// </summary>
public interface IInlineHttpInvoker
{
    /// <summary>
    /// Sends a single HTTP request derived from the supplied method info and
    /// returns the response in the same shape as <see cref="IBowireProtocol.InvokeAsync"/>.
    /// </summary>
    Task<InvokeResult> InvokeHttpAsync(
        string serverUrl,
        BowireMethodInfo methodInfo,
        List<string> jsonMessages,
        Dictionary<string, string>? metadata,
        CancellationToken ct = default);
}

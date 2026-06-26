// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Per-host configuration for <see cref="BowireInterceptorMiddleware"/>.
/// The host hands an instance to <c>UseBowireInterceptor()</c>; the
/// middleware honours it for every request that passes through.
/// </summary>
/// <remarks>
/// <para>
/// All options have sensible defaults that match the acceptance criteria
/// on the issue (#153): 1 MB body cap, in-process recording disabled
/// until Phase B's auto-record hook lands, the Bowire workbench's own
/// API endpoints excluded so the rail never observes itself.
/// </para>
/// </remarks>
public sealed class BowireInterceptorOptions
{
    /// <summary>
    /// Maximum number of bytes of request / response body the middleware
    /// will capture per side. Bodies larger than this are recorded up to
    /// the cap with <see cref="InterceptedFlow.RequestBodyTruncated"/> /
    /// <see cref="InterceptedFlow.ResponseBodyTruncated"/> set. Default 1 MiB.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Maximum number of intercepted flows the in-memory ring buffer
    /// retains. Older flows are evicted FIFO. Default 1000 — same as the
    /// proxy capture store.
    /// </summary>
    public int MaxRetainedFlows { get; set; } = 1000;

    /// <summary>
    /// Path prefixes the middleware does not observe. Default is the
    /// workbench's own surface (<c>/bowire</c>) plus its API endpoints so
    /// turning the interceptor on does not flood the rail with self-traffic.
    /// Operators can extend the list (e.g. to mute a noisy health-check
    /// route) by mutating it before passing the options to
    /// <c>UseBowireInterceptor</c>.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively against the request path. A prefix
    /// match is sufficient: <c>/bowire</c> covers <c>/bowire/api/foo</c>.
    /// </remarks>
    public List<string> IgnoredPathPrefixes { get; } = new()
    {
        "/bowire",
    };

    /// <summary>
    /// Master kill-switch — when false the middleware short-circuits
    /// immediately with no body buffering, no stream wrapping, no
    /// store write. Useful for ops-driven feature flagging without
    /// re-deploying.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

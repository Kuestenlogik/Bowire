// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Process-singleton flag that records whether
/// <c>app.UseBowireInterceptor()</c> ran during the host's startup.
/// The workbench's Intercept rail probes this through
/// <c>GET /api/intercepted/status</c> to decide whether the Captured /
/// Live overrides / Settings sub-tabs render their normal content or
/// the v2.2 "activate the interceptor first" empty state.
/// </summary>
/// <remarks>
/// <para>
/// Embedded hosts that opt in via <c>UseBowireInterceptor()</c> flip
/// <see cref="Activated"/> to true once. Standalone Bowire (the CLI
/// tool) reaches activation a different way — by starting an
/// in-process reverse-proxy via the Topbar "Reverse proxy" surface —
/// so the status endpoint OR-folds this flag with the
/// <see cref="ReverseProxyRegistry"/>'s running-host count. Either
/// signal counts as "interceptor active for this workbench's
/// purposes".
/// </para>
/// <para>
/// Lifetime is the host process. There is no de-activation path —
/// once the middleware is wired into the pipeline, it stays wired
/// until the process exits. That matches the operator's mental model
/// in both deployment modes.
/// </para>
/// </remarks>
public sealed class InterceptorActivation
{
    private int _activated;

    /// <summary>Whether <c>UseBowireInterceptor()</c> ran on this host.</summary>
    public bool Activated => Volatile.Read(ref _activated) != 0;

    /// <summary>Mark the interceptor as activated. Idempotent.</summary>
    public void MarkActivated() => Interlocked.Exchange(ref _activated, 1);
}

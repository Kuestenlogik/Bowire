// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Thrown by an <see cref="IAuthFlowCapturer"/> when a flow is misconfigured,
/// the login chain fails, or no token is captured. A Core-visible type so the
/// auth-recording endpoint can surface a clean error without Core referencing
/// the optional Scanner sibling (whose own <c>AuthFlowException</c> Core can't
/// name).
/// </summary>
public sealed class AuthFlowCaptureException : Exception
{
    public AuthFlowCaptureException(string message) : base(message) { }
    public AuthFlowCaptureException(string message, Exception inner) : base(message, inner) { }
    public AuthFlowCaptureException() { }
}

/// <summary>
/// The credential produced by running an auth flow (#563 flow-capture): the
/// captured token plus the scheme/header it should be presented under.
/// </summary>
/// <param name="Credential">The captured credential (bearer/basic token or api-key).</param>
/// <param name="Scheme">Derived scheme — bearer / basic / apikey — or null to keep the caller's default.</param>
/// <param name="Header">Header the credential is presented in, or null for the default.</param>
public sealed record AuthFlowCaptureResult(string Credential, string? Scheme, string? Header);

/// <summary>
/// Runs an auth-flow definition (a scriptable login → token chain) and returns
/// the captured credential (#563). This is the seam that keeps the outbound
/// flow-execution — which lives in the optional Kuestenlogik.Bowire.Security.Scanner
/// sibling — reachable from Core's auth-recording endpoint and the MCP tools
/// without Core referencing the Scanner. Hosts that don't wire it up simply
/// don't offer flow-capture (the store's static-credential capture still works).
/// </summary>
/// <remarks>
/// <see cref="CaptureAsync"/> makes OUTBOUND HTTP calls (it executes the login
/// chain), so it only ever runs as a direct, explicit result of an operator
/// action — a CLI invocation, a workbench click, or a confirmed MCP tool call.
/// </remarks>
public interface IAuthFlowCapturer
{
    /// <summary>
    /// Execute the flow described by <paramref name="flowJson"/> and return the
    /// captured credential. Throws when the flow is misconfigured or yields no
    /// token (fail closed — never returns an empty credential).
    /// </summary>
    Task<AuthFlowCaptureResult> CaptureAsync(string flowJson, CancellationToken ct = default);
}

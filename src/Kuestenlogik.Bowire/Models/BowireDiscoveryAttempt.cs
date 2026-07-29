// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// One protocol plugin's turn at a discovery probe (#534). The old
/// <c>attempts</c> extension on the <c>/api/services</c> ProblemDetails
/// body was a flat <c>string[]</c> that only listed the plugins which
/// <em>threw</em> — a plugin that ran cleanly and returned nothing was
/// invisible, which is the single most common "why is my sidebar empty?"
/// case. Every probed plugin now contributes exactly one of these,
/// whatever the result, so the UI, the CLI and an MCP agent can all
/// answer "who tried what, and what came back?" from the same record.
/// </summary>
/// <param name="PluginId">
/// The plugin's <see cref="IBowireProtocol.Id"/> ("grpc", "rest", …).
/// Stable machine key — use this for filtering / grouping.
/// </param>
/// <param name="Plugin">
/// The plugin's <see cref="IBowireProtocol.Name"/> — the display name
/// shown to the operator.
/// </param>
/// <param name="Outcome">
/// One of four values:
/// <list type="bullet">
///   <item><c>ok</c> — the plugin returned at least one service.</item>
///   <item><c>empty</c> — the plugin ran to completion and returned
///         nothing. Not an error: the URL simply is not this plugin's.</item>
///   <item><c>error</c> — the probe threw (connection refused, TLS
///         failure, malformed schema, …). <see cref="Message"/> carries
///         the exception text.</item>
///   <item><c>timeout</c> — the probe was cancelled because it exceeded
///         the per-probe ceiling, or the caller aborted the request.</item>
/// </list>
/// </param>
/// <param name="ServicesFound">Number of services this plugin contributed.</param>
/// <param name="DurationMs">Wall-clock milliseconds the probe took.</param>
/// <param name="Message">
/// Human-readable one-liner. Never prefixed with the plugin name —
/// <see cref="Plugin"/> already carries it, and a prefixed message
/// renders as "gRPC — gRPC: connection refused" in the UI.
/// </param>
public sealed record BowireDiscoveryAttempt(
    string PluginId,
    string Plugin,
    string Outcome,
    int ServicesFound,
    long DurationMs,
    string Message)
{
    /// <summary>The plugin returned at least one service.</summary>
    public const string OutcomeOk = "ok";

    /// <summary>The plugin ran cleanly but returned no services.</summary>
    public const string OutcomeEmpty = "empty";

    /// <summary>The plugin's probe threw.</summary>
    public const string OutcomeError = "error";

    /// <summary>The plugin's probe was cancelled (per-probe ceiling or caller abort).</summary>
    public const string OutcomeTimeout = "timeout";
}

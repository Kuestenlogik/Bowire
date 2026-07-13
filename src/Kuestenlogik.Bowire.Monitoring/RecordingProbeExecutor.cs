// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// The wire-level <see cref="IProbeExecutor"/> — replays a probe's saved
/// <see cref="BowireRecording"/> through the same protocol plugins the workbench
/// uses. Each step is dispatched via <see cref="IBowireProtocol.InvokeAsync"/>;
/// the last step's status + response become the probe's result and the summed
/// plugin-reported durations its latency. A missing plugin or a transport
/// failure surfaces as <see cref="ProbeExecutionException"/> so the runner
/// records an <see cref="ProbeResult.Error"/> outcome.
/// </summary>
/// <remarks>
/// This is where Monitoring leaves the Core-slim boundary and touches the
/// protocol registry; the host supplies the registry (the standalone CLI
/// discovers the installed plugins). Streaming steps aren't a health-probe
/// shape and are dispatched through the unary path like everything else — a
/// probe author points a probe at a unary call.
/// </remarks>
public sealed class RecordingProbeExecutor : IProbeExecutor
{
    private readonly BowireProtocolRegistry _registry;
    private readonly bool _showInternalServices;

    public RecordingProbeExecutor(BowireProtocolRegistry registry, bool showInternalServices = false)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _showInternalServices = showInternalServices;
    }

    /// <inheritdoc/>
    public async Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var steps = probe.Recording.Steps;
        if (steps.Count == 0)
        {
            throw new ProbeExecutionException($"Probe '{probe.Name}' has a recording with no steps to replay.");
        }

        long totalDuration = 0;
        string statusText = "OK";
        string? body = null;

        foreach (var step in steps)
        {
            var protocol = _registry.GetById(step.Protocol);
            if (protocol is null)
            {
                throw new ProbeExecutionException(
                    $"Probe '{probe.Name}' step '{step.Id}' needs protocol plugin '{step.Protocol}', which isn't loaded.");
            }

            var result = await InvokeStepAsync(protocol, step, probe.Name, ct).ConfigureAwait(false);
            totalDuration += result.DurationMs;
            statusText = result.Status;
            body = result.Response;
        }

        return new ProbeExecutionResult(ParseStatus(statusText), totalDuration, body);
    }

    /// <summary>
    /// The single boundary against the unbounded 3rd-party plugin invoke
    /// surface. A cancelled run propagates (it is not a probe failure);
    /// everything else is wrapped into <see cref="ProbeExecutionException"/> so
    /// the runner records an <see cref="ProbeResult.Error"/> outcome and the
    /// scheduler loop survives. The general catch is isolated here on purpose.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sole boundary against unbounded 3rd-party plugin transport; wrapped into ProbeExecutionException. Cancellation still propagates.")]
    private async Task<InvokeResult> InvokeStepAsync(
        IBowireProtocol protocol, BowireRecordingStep step, string probeName, CancellationToken ct)
    {
        var messages = string.IsNullOrEmpty(step.Body) ? ["{}"] : new List<string> { step.Body };
        var metadata = step.Metadata is null ? null : new Dictionary<string, string>(step.Metadata);
        try
        {
            return await protocol.InvokeAsync(
                step.ServerUrl ?? string.Empty,
                step.Service,
                step.Method,
                messages,
                _showInternalServices,
                metadata,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProbeExecutionException($"Probe '{probeName}' step '{step.Id}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Coerce a plugin's status string into an integer the status assertion can
    /// compare. Numeric statuses (REST) parse directly; the gRPC-style
    /// <c>"OK"</c> maps to 200; anything else is 0 (treated as not-2xx).
    /// </summary>
    internal static int ParseStatus(string status)
    {
        if (int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return code;
        if (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)) return 200;
        return 0;
    }
}

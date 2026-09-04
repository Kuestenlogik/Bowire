// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Telemetry;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire;

/// <summary>
/// The registry fan-out behind every discovery surface Bowire has (#534):
/// the <c>/api/services</c> endpoint, <c>bowire discover</c> on the CLI,
/// and the <c>bowire.discover</c> MCP tool. Each of those used to own a
/// private copy of "loop the plugins, call DiscoverAsync, swallow what
/// throws" — and each swallowed a different amount of the diagnosis, so
/// the three surfaces disagreed about why a URL produced nothing.
/// <para>
/// One pass produces two things: the merged service list, and one
/// <see cref="BowireDiscoveryAttempt"/> per probed plugin — including the
/// plugins that ran cleanly and found nothing, which is precisely the case
/// the old error-only reporting hid.
/// </para>
/// <para>
/// Static because it is pure: no cache, no ring buffer, no registry of its
/// own. Every input arrives as a parameter. Should it ever need to
/// remember something between calls, it has to become an injected service
/// instead. <see cref="IBowireDiscoveryDiagnostics"/> (#544) does not
/// change that: the probe <em>reads</em> a diagnostic off the return value
/// of the same await it was already making, into a local that dies with
/// the task. It stores nothing, and it never goes back to a plugin to ask
/// what happened.
/// </para>
/// </summary>
public static class BowireDiscoveryProbe
{
    /// <summary>
    /// Probe <paramref name="serverUrl"/> with every registered protocol
    /// plugin (or just the hinted one) in parallel and report what each
    /// one found.
    /// </summary>
    /// <param name="registry">The protocol registry to fan out over.</param>
    /// <param name="serverUrl">
    /// The bare target URL — <em>without</em> a <c>hint@</c> prefix. Split
    /// it with <see cref="BowireServerUrl.Parse"/> first and pass the hint
    /// separately.
    /// </param>
    /// <param name="pluginHint">
    /// When non-null, only the plugin whose <see cref="IBowireProtocol.Id"/>
    /// matches (case-insensitively) is probed. Saves the ~12 s cost of
    /// probing every plugin against a URL the caller already knows the
    /// owner of. An unknown hint yields zero attempts — callers that need
    /// to tell "unknown hint" apart from "no plugins loaded" check the
    /// registry themselves.
    /// </param>
    /// <param name="showInternalServices">Forwarded to each plugin's DiscoverAsync.</param>
    /// <param name="perProbeCeiling">
    /// Hard cap on any single plugin's probe. Plugins enforce their own
    /// timeouts, but a wedged one would otherwise drag the whole fan-out
    /// past the browser's 12 s abort — total wall-clock is
    /// max(per-probe), not the sum, because the probes run in parallel.
    /// </param>
    /// <param name="metadata">
    /// Plugin configuration for this probe — a gRPC descriptor set, for a
    /// server that does not answer reflection — so a target the plain
    /// discovery path cannot enumerate still reports its services. Forwarded
    /// verbatim; a plugin with nothing to read in it ignores it.
    /// </param>
    /// <param name="logger">Optional; receives one warning per failed probe.</param>
    /// <param name="ct">Caller cancellation, linked into the ceiling.</param>
    public static async Task<BowireDiscoveryProbeResult> RunAsync(
        BowireProtocolRegistry registry,
        string serverUrl,
        string? pluginHint,
        bool showInternalServices,
        TimeSpan perProbeCeiling,
        IReadOnlyDictionary<string, string>? metadata = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Materialised so the caller's terminal diagnostics can tell
        // "no plugin matched the hint" apart from "no plugins at all".
        var protocolsToProbe = (pluginHint is null
            ? registry.Protocols
            : registry.Protocols.Where(p =>
                string.Equals(p.Id, pluginHint, StringComparison.OrdinalIgnoreCase))).ToList();

        var services = new List<BowireServiceInfo>();
        var attempts = new List<BowireDiscoveryAttempt>(protocolsToProbe.Count);
        if (protocolsToProbe.Count == 0)
            return new BowireDiscoveryProbeResult(services, attempts);

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(perProbeCeiling);
        var probeCt = probeCts.Token;
        var ceilingSeconds = perProbeCeiling.TotalSeconds;

        var probeTasks = protocolsToProbe.Select(async protocol =>
        {
            var probeStart = Stopwatch.GetTimestamp();
            var outcome = BowireDiscoveryAttempt.OutcomeOk;
            var found = new List<BowireServiceInfo>();
            // A local, read from the return value of the await below and
            // dead when this task ends. Two concurrent probes of the same
            // plugin instance for two URLs cannot see each other's — which
            // is the whole reason the channel is a return value and not a
            // "ask the plugin afterwards" lookup (#544).
            BowireDiscoveryDiagnostic? diagnostic = null;
            string message;
            try
            {
                if (protocol is IBowireDiscoveryDiagnostics reporter)
                {
                    var report = await reporter.DiscoverWithDiagnosticsAsync(
                        serverUrl, showInternalServices, probeCt);
                    found = report.Services;
                    diagnostic = report.Diagnostic;
                }
                else
                {
                    found = await protocol.DiscoverAsync(serverUrl, showInternalServices, metadata, probeCt);
                }

                foreach (var svc in found)
                {
                    svc.Source = protocol.Id;
                    // Tag every service with its origin URL so multi-URL setups
                    // can route invocations back to the right base. Plugins may
                    // have already set this (e.g. REST does); we only fill it in
                    // when missing.
                    svc.OriginUrl ??= serverUrl;
                }

                if (found.Count == 0)
                {
                    // "Ran and found nothing" is the outcome the old
                    // error-only reporting dropped on the floor, and it is
                    // the most common one — every plugin that doesn't own
                    // the URL lands here.
                    outcome = BowireDiscoveryAttempt.OutcomeEmpty;
                    message = "returned no services";
                }
                else
                {
                    message = $"{found.Count} service{(found.Count == 1 ? "" : "s")}";
                }

                // A plugin that reported something gets to overwrite both,
                // because "5 services" is a worse answer than "5 services,
                // but tools/list returned a payload this MCP revision
                // rejects". Runs before the telemetry block below so the
                // `outcome` tag picks `partial` up for free.
                if (diagnostic is not null)
                    (outcome, message) = ApplyDiagnostic(diagnostic, found.Count);
            }
            // Plugin DiscoverAsync calls into third-party transports
            // (HTTP, gRPC reflection, MQTT broker connect, ...) and can
            // throw anything from HttpRequestException to SocketException
            // to plugin-author-defined types. The fan-out MUST tolerate
            // any one plugin's failure and report it as an attempt
            // instead of poisoning the whole result. CA1031 is switched
            // off repo-wide in .editorconfig for exactly this shape — no
            // pragma needed.
            catch (Exception ex)
            {
                found = [];
                // The exception is the better diagnosis — it says what
                // actually stopped the probe. Anything the plugin managed
                // to report before throwing is superseded, so it must not
                // leave its `details` hanging off an `error` attempt whose
                // message now comes from somewhere else.
                diagnostic = null;
                if (ex is OperationCanceledException)
                {
                    outcome = BowireDiscoveryAttempt.OutcomeTimeout;
                    message = ct.IsCancellationRequested
                        ? "discovery was cancelled by the caller"
                        : $"probe exceeded the {ceilingSeconds:0.#} s ceiling";
                }
                else
                {
                    outcome = BowireDiscoveryAttempt.OutcomeError;
                    // No "{plugin}: " prefix — the attempt record already
                    // carries the plugin name, and a prefixed message
                    // renders as "gRPC — gRPC: connection refused".
                    message = ex.Message;
                }

                if (logger is not null)
                {
                    logger.LogWarning(ex,
                        "Discovery failed for protocol {Protocol} at {ServerUrl}",
                        protocol.Name, BowireEndpointHelpers.SafeLog(serverUrl));
                }
            }

            var elapsedMs = (long)((Stopwatch.GetTimestamp() - probeStart)
                / (double)Stopwatch.Frequency * 1000.0);

            BowireTelemetry.DiscoverCount.Add(1, new TagList
            {
                { "protocol", protocol.Id },
                { "outcome", outcome },
                { "services_found", found.Count },
            });

            // Id and Name come from a plugin, so they are only as
            // non-null as its author made them. The attempt table pads and
            // sorts on both — `bowire discover` measures the column width
            // with `.Length` — so a plugin that returns null takes the
            // command down while rendering the very table that exists to
            // explain what plugins did. Substituting here keeps every
            // consumer (CLI table, workbench diagnostics) safe at once.
            return (Services: found, Attempt: new BowireDiscoveryAttempt(
                protocol.Id ?? "(unknown)", protocol.Name ?? protocol.Id ?? "(unnamed plugin)",
                outcome, found.Count, elapsedMs, message)
            {
                Details = diagnostic?.Details,
            });
        }).ToArray();

        var probeResults = await Task.WhenAll(probeTasks);
        foreach (var (found, attempt) in probeResults)
        {
            services.AddRange(found);
            attempts.Add(attempt);
        }

        return new BowireDiscoveryProbeResult(services, attempts);
    }

    /// <summary>
    /// Fold a plugin's <see cref="BowireDiscoveryDiagnostic"/> and the
    /// number of services it produced into the outcome + message the
    /// attempt carries (#544). Pure: the probe, not the plugin, owns the
    /// outcome vocabulary, because only the probe has both halves.
    /// <list type="bullet">
    ///   <item>Fault + services → <c>partial</c>. The tree is populated but
    ///         incomplete, which is neither <c>ok</c> nor <c>error</c>.</item>
    ///   <item>Fault + nothing → <c>error</c>. Indistinguishable from a
    ///         throw, so it reports as one.</item>
    ///   <item>Note → the outcome the service count alone would have
    ///         produced; only the message improves.</item>
    /// </list>
    /// The service count stays in the message on every branch because the
    /// CLI table and the workbench rows print <c>Message</c>, not
    /// <c>ServicesFound</c>.
    /// </summary>
    private static (string Outcome, string Message) ApplyDiagnostic(
        BowireDiscoveryDiagnostic diagnostic, int found)
    {
        var plural = found == 1 ? "" : "s";
        return (diagnostic.Severity, found) switch
        {
            (BowireDiscoverySeverity.Fault, > 0) => (
                BowireDiscoveryAttempt.OutcomePartial,
                $"{found} service{plural}, but {diagnostic.Message}"),
            (BowireDiscoverySeverity.Fault, _) => (
                BowireDiscoveryAttempt.OutcomeError,
                diagnostic.Message),
            (_, > 0) => (
                BowireDiscoveryAttempt.OutcomeOk,
                $"{found} service{plural} — {diagnostic.Message}"),
            _ => (
                BowireDiscoveryAttempt.OutcomeEmpty,
                diagnostic.Message),
        };
    }
}

/// <summary>
/// What one <see cref="BowireDiscoveryProbe.RunAsync"/> pass produced:
/// the merged services from every plugin that found something, plus one
/// <see cref="BowireDiscoveryAttempt"/> per probed plugin regardless of
/// outcome. <see cref="Attempts"/> is populated even when
/// <see cref="Services"/> is not — that is the whole point.
/// </summary>
public sealed record BowireDiscoveryProbeResult(
    List<BowireServiceInfo> Services,
    List<BowireDiscoveryAttempt> Attempts);

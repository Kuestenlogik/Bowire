// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Parallel;

/// <summary>
/// In-process runner for parallel sessions. Each session walks a
/// round-robin slice of the target list sequentially; sessions run
/// concurrently. Mirrors the JS-side Phase 1 runner so the wire
/// behaviour stays uniform whether the operator runs the load from
/// the browser or fans it out to a remote Bowire host.
/// <para>
/// Implementation notes:
/// </para>
/// <list type="bullet">
///   <item><description>
///   Uses one shared <see cref="HttpClient"/> built via
///   <see cref="BowireHttpClientFactory.Create"/> so loopback-cert
///   relaxation behaves the same as every other protocol plugin's
///   outbound client.
///   </description></item>
///   <item><description>
///   Stagger: when <c>rampUpSeconds &gt; 0</c>, session k waits
///   <c>k * rampUpSeconds / sessionCount</c> seconds before starting.
///   Even spread is the workhorse pattern operators want — Poisson /
///   ramp-curve options can land in a follow-up.
///   </description></item>
///   <item><description>
///   Failure policy: <c>continueOnError = false</c> trips a shared
///   <see cref="CancellationTokenSource"/> on the first non-pass,
///   draining in-flight session loops. Already-completed targets
///   stay in the result list so the operator can see how far each
///   session got before the abort.
///   </description></item>
///   <item><description>
///   Env policy: when <c>envPool</c> is non-empty, session k is
///   assigned <c>pool[k % pool.Count]</c> and the worker stamps an
///   <c>X-Bowire-Env</c> header on every upstream call for that
///   session. The env id also surfaces on the per-session result so
///   the operator can attribute failures back to a specific slot.
///   </description></item>
/// </list>
/// </summary>
internal static class BowireParallelRunner
{
    /// <summary>
    /// Run <paramref name="request"/> in-process and return the
    /// aggregate result. Honours the request's
    /// <see cref="BowireParallelLocalRequest.RampUpSeconds"/> and
    /// <see cref="BowireParallelLocalRequest.ContinueOnError"/>; the
    /// outer cancellation token (from the HTTP request) merges with
    /// the abort-on-first-failure token so an HTTP disconnect still
    /// cleanly tears down every running session.
    /// </summary>
    public static async Task<BowireParallelResponse> RunAsync(
        BowireParallelLocalRequest request,
        IConfiguration? configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defensive normalisation — the wire types use loose defaults
        // (sessionCount defaults to 1, EnvPool may be null). We
        // collapse those here so the loop body stays linear.
        var sessionCount = Math.Max(1, request.SessionCount);
        var targets = request.Targets ?? [];
        var rampUpSeconds = Math.Max(0, request.RampUpSeconds);
        var continueOnError = request.ContinueOnError;
        var envPool = request.EnvPool ?? [];

        var overall = Stopwatch.StartNew();

        // One client per run (not per session) — HTTP/2 multiplexing
        // + connection pooling handle concurrency. A new client per
        // session would defeat the pool and burn socket handles.
        using var http = BowireHttpClientFactory.Create(configuration, "parallel",
            timeout: TimeSpan.FromMinutes(2));

        // continueOnError = false → first failure triggers cancellation
        // for every in-flight session. The token also merges with the
        // ambient request-aborted token from the caller.
        using var abortOnFailureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var sessionResults = new BowireParallelSessionResult[sessionCount];
        var allTargetResults = new List<BowireParallelTargetResult>(targets.Count * sessionCount);
        var targetResultsLock = new object();

        var tasks = new Task[sessionCount];
        for (var i = 0; i < sessionCount; i++)
        {
            var sessionIndex = i;
            var envId = envPool.Count > 0 ? envPool[sessionIndex % envPool.Count] : null;
            // Even-spread stagger. Session 0 always starts immediately
            // (delayMs=0); session N-1 starts at rampUp * (N-1)/N — so
            // the last session has a chance to run before the run
            // would otherwise have finished. Subdividing by
            // sessionCount (not sessionCount-1) keeps a 1-session run
            // a no-op when rampUp > 0.
            var delayMs = sessionCount > 1
                ? (int)Math.Round(rampUpSeconds * 1000.0 * sessionIndex / sessionCount)
                : 0;
            tasks[sessionIndex] = Task.Run(async () =>
            {
                var perSession = await RunSessionAsync(
                    sessionIndex,
                    envId,
                    targets,
                    sessionCount,
                    delayMs,
                    continueOnError,
                    abortOnFailureCts,
                    http,
                    logger,
                    abortOnFailureCts.Token).ConfigureAwait(false);
                sessionResults[sessionIndex] = perSession.Summary;
                lock (targetResultsLock)
                {
                    allTargetResults.AddRange(perSession.Targets);
                }
            }, abortOnFailureCts.Token);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the outer caller disconnected or the abort-on-
            // first-failure path tripped. Either way we collected
            // whatever sessions did finish; fall through to the
            // aggregate.
        }

        overall.Stop();

        // Sort per-target results by (session, target) so the response
        // ordering is stable regardless of which session finished
        // first.
        allTargetResults.Sort((a, b) =>
        {
            var s = a.SessionIndex.CompareTo(b.SessionIndex);
            return s != 0 ? s : a.TargetIndex.CompareTo(b.TargetIndex);
        });

        var pass = allTargetResults.Count(r => r.Pass);
        var fail = allTargetResults.Count - pass;

        return new BowireParallelResponse
        {
            SessionCount = sessionCount,
            TargetCount = targets.Count,
            TotalDurationMs = overall.ElapsedMilliseconds,
            PassCount = pass,
            FailCount = fail,
            Sessions = [.. sessionResults.Where(s => s is not null)],
            Results = allTargetResults,
        };
    }

    private sealed class SessionOutcome
    {
        public required BowireParallelSessionResult Summary { get; init; }
        public required List<BowireParallelTargetResult> Targets { get; init; }
    }

    private static async Task<SessionOutcome> RunSessionAsync(
        int sessionIndex,
        string? envId,
        List<BowireParallelTarget> targets,
        int sessionCount,
        int delayMs,
        bool continueOnError,
        CancellationTokenSource abortOnFailureCts,
        HttpClient http,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var perSessionTargets = new List<BowireParallelTargetResult>();
        var summary = new BowireParallelSessionResult
        {
            SessionIndex = sessionIndex,
            EnvId = envId,
        };
        var sessionSw = Stopwatch.StartNew();

        if (delayMs > 0)
        {
            try
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                summary.Aborted = "cancelled-before-start";
                summary.DurationMs = sessionSw.ElapsedMilliseconds;
                return new SessionOutcome { Summary = summary, Targets = perSessionTargets };
            }
        }

        // Round-robin slice — same shape Phase 1's JS runner uses.
        // Session k owns target indices where index % N == k.
        for (var t = sessionIndex; t < targets.Count; t += sessionCount)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                summary.Aborted = "cancelled";
                break;
            }

            var target = targets[t];
            var result = await ExecuteTargetAsync(
                sessionIndex, t, envId, target, http, logger, cancellationToken)
                .ConfigureAwait(false);
            perSessionTargets.Add(result);

            if (result.Pass)
            {
                summary.PassCount++;
            }
            else
            {
                summary.FailCount++;
                if (!continueOnError)
                {
                    summary.Aborted = "first-failure";
                    // Trip the shared CTS so every other session
                    // unwinds at its next cancellation check.
                    try { await abortOnFailureCts.CancelAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { /* already done */ }
                    break;
                }
            }
        }

        sessionSw.Stop();
        summary.DurationMs = sessionSw.ElapsedMilliseconds;
        return new SessionOutcome { Summary = summary, Targets = perSessionTargets };
    }

    private static async Task<BowireParallelTargetResult> ExecuteTargetAsync(
        int sessionIndex,
        int targetIndex,
        string? envId,
        BowireParallelTarget target,
        HttpClient http,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrEmpty(target.Url))
            {
                sw.Stop();
                return new BowireParallelTargetResult
                {
                    SessionIndex = sessionIndex,
                    TargetIndex = targetIndex,
                    Label = target.Label,
                    Pass = false,
                    Status = 0,
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = "Missing target URL",
                };
            }

            using var req = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrEmpty(target.Method) ? "POST" : target.Method),
                target.Url);
            if (!string.IsNullOrEmpty(target.Body))
            {
                req.Content = new StringContent(target.Body, Encoding.UTF8, "application/json");
            }
            if (target.Headers is not null)
            {
                foreach (var (k, v) in target.Headers)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    // Content-Type / Content-Length live on the
                    // content's header collection, not the request's;
                    // TryAddWithoutValidation on the request swallows
                    // the failure silently otherwise.
                    if (string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)
                        && req.Content is not null)
                    {
                        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(v);
                        continue;
                    }
                    req.Headers.TryAddWithoutValidation(k, v);
                }
            }
            if (!string.IsNullOrEmpty(envId))
            {
                // Per-session env tag — surfaces on the upstream so a
                // server that splits state per env stays honest. The
                // operator chooses the pool from the UI; we just
                // forward the slot id.
                req.Headers.TryAddWithoutValidation("X-Bowire-Env", envId);
            }

            using var resp = await http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            // Drain the body so the next request on this connection
            // doesn't read into our headers — cheap because we don't
            // hold it.
            await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            sw.Stop();
            var status = (int)resp.StatusCode;
            return new BowireParallelTargetResult
            {
                SessionIndex = sessionIndex,
                TargetIndex = targetIndex,
                Label = target.Label,
                Pass = resp.IsSuccessStatusCode,
                Status = status,
                DurationMs = sw.ElapsedMilliseconds,
                Error = resp.IsSuccessStatusCode ? null : resp.ReasonPhrase,
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new BowireParallelTargetResult
            {
                SessionIndex = sessionIndex,
                TargetIndex = targetIndex,
                Label = target.Label,
                Pass = false,
                Status = 0,
                DurationMs = sw.ElapsedMilliseconds,
                Error = "cancelled",
            };
        }
#pragma warning disable CA1031 // We want to surface every upstream failure as a target row, not let it nuke the session task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            sw.Stop();
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex,
                    "Parallel target s={Session}/t={Target} failed",
                    sessionIndex, targetIndex);
            }
            return new BowireParallelTargetResult
            {
                SessionIndex = sessionIndex,
                TargetIndex = targetIndex,
                Label = target.Label,
                Pass = false,
                Status = 0,
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message,
            };
        }
    }
}

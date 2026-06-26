// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Parallel;

/// <summary>
/// Fan-out coordinator for #132 Phase 2. Takes a distributed-run
/// request (targets + total session count + host list) and shards
/// the sessions across the listed hosts by POSTing
/// <c>/api/parallel/start-local</c> on each. Aggregates the per-host
/// responses into one merged <see cref="BowireParallelResponse"/>
/// the UI can render as a single run.
/// <para>
/// Sharding strategy: even spread with overflow. With N sessions and
/// H hosts, each host gets <c>N / H</c> sessions plus one extra for
/// the first <c>N % H</c> hosts. Targets + ramp-up + failure policy
/// are forwarded verbatim to every host; the env pool is forwarded as-
/// is so each host can still cycle through it per session (the pool
/// is small and identical across hosts by design).
/// </para>
/// <para>
/// Auth: the coordinator forwards <c>Authorization: Bearer {token}</c>
/// on each outbound call when a token is supplied (request body or
/// <c>BOWIRE_PARALLEL_TOKEN</c> env var). The browser-driven path
/// never sees the token — the JS layer hits the coordinator over
/// loopback and the coordinator process holds the secret.
/// </para>
/// </summary>
internal static class BowireParallelCoordinator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<BowireParallelResponse> RunAsync(
        BowireParallelDistributedRequest request,
        IConfiguration? configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hosts = (request.Hosts ?? [])
            .Select(h => (h ?? string.Empty).Trim().TrimEnd('/'))
            .Where(h => !string.IsNullOrEmpty(h))
            .ToList();

        // No hosts → degenerate to a pure in-process run. Lets the
        // workbench send the same wire shape regardless of whether
        // the operator filled in the Hosts field.
        if (hosts.Count == 0)
        {
            return await BowireParallelRunner.RunAsync(
                request, configuration, logger, cancellationToken).ConfigureAwait(false);
        }

        var totalSessions = Math.Max(1, request.SessionCount);
        var token = request.Token
            ?? Environment.GetEnvironmentVariable("BOWIRE_PARALLEL_TOKEN");

        var overall = Stopwatch.StartNew();

        using var http = BowireHttpClientFactory.Create(configuration, "parallel-coordinator",
            timeout: TimeSpan.FromMinutes(5));

        // Even spread + overflow: hosts 0..(N%H-1) get one extra
        // session each. With 5 sessions across 2 hosts → [3, 2].
        var perHostSessions = new int[hosts.Count];
        var baseShare = totalSessions / hosts.Count;
        var overflow = totalSessions % hosts.Count;
        for (var i = 0; i < hosts.Count; i++)
        {
            perHostSessions[i] = baseShare + (i < overflow ? 1 : 0);
        }

        var fanOutTasks = new Task<(BowireParallelHostSummary Summary, BowireParallelResponse? Body)>[hosts.Count];
        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            var hostSessions = perHostSessions[i];
            // Skip hosts that got 0 sessions (more hosts than
            // sessions) — sending a 0-session request would waste a
            // round-trip and pollute the result list with an empty
            // host roll-up.
            if (hostSessions == 0)
            {
                fanOutTasks[i] = Task.FromResult<(BowireParallelHostSummary, BowireParallelResponse?)>((
                    new BowireParallelHostSummary
                    {
                        Host = host,
                        SessionCount = 0,
                        PassCount = 0,
                        FailCount = 0,
                        DurationMs = 0,
                    }, null));
                continue;
            }
            var perHostRequest = new BowireParallelLocalRequest
            {
                Targets = request.Targets,
                SessionCount = hostSessions,
                RampUpSeconds = request.RampUpSeconds,
                ContinueOnError = request.ContinueOnError,
                EnvPool = request.EnvPool,
            };
            // CA2025: the using-scope for `http` extends past the
            // Task.WhenAll below — every task is awaited before the
            // method returns, so the client never disposes while a
            // SendAsync is in flight. The analyzer can't model that
            // ownership transfer; suppress with the narrowest scope.
#pragma warning disable CA2025
            fanOutTasks[i] = CallHostAsync(http, host, perHostRequest, token, logger, cancellationToken);
#pragma warning restore CA2025
        }

        var hostResults = await Task.WhenAll(fanOutTasks).ConfigureAwait(false);
        overall.Stop();

        // Merge — re-index session indices so two hosts that both
        // ran "session #0" land at distinct slots in the merged
        // response. The session ordering follows hosts[] order so the
        // operator's mental model (host A's sessions, then host B's)
        // matches the result list.
        var merged = new BowireParallelResponse
        {
            SessionCount = totalSessions,
            TargetCount = request.Targets?.Count ?? 0,
            TotalDurationMs = overall.ElapsedMilliseconds,
            Hosts = [],
        };
        var sessionOffset = 0;
        foreach (var (summary, body) in hostResults)
        {
            merged.Hosts!.Add(summary);
            if (body is null) { sessionOffset += summary.SessionCount; continue; }
            foreach (var s in body.Sessions)
            {
                merged.Sessions.Add(new BowireParallelSessionResult
                {
                    SessionIndex = sessionOffset + s.SessionIndex,
                    EnvId = s.EnvId,
                    PassCount = s.PassCount,
                    FailCount = s.FailCount,
                    DurationMs = s.DurationMs,
                    Aborted = s.Aborted,
                });
            }
            foreach (var r in body.Results)
            {
                merged.Results.Add(new BowireParallelTargetResult
                {
                    SessionIndex = sessionOffset + r.SessionIndex,
                    TargetIndex = r.TargetIndex,
                    Label = r.Label,
                    Pass = r.Pass,
                    Status = r.Status,
                    DurationMs = r.DurationMs,
                    Error = r.Error,
                });
            }
            merged.PassCount += body.PassCount;
            merged.FailCount += body.FailCount;
            sessionOffset += summary.SessionCount;
        }

        return merged;
    }

    private static async Task<(BowireParallelHostSummary, BowireParallelResponse?)> CallHostAsync(
        HttpClient http,
        string host,
        BowireParallelLocalRequest body,
        string? token,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var summary = new BowireParallelHostSummary
        {
            Host = host,
            SessionCount = body.SessionCount,
        };
        try
        {
            // Hosts may run with or without a /bowire prefix — we
            // hit the standalone-shaped /api/parallel/start-local
            // first; an embedded host is expected to be addressed
            // by including its prefix in the configured URL (e.g.
            // https://node-eu.example/bowire). Keeps the wire
            // simple — no extra "what's your prefix?" round-trip.
            var url = host + "/api/parallel/start-local";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            var payload = JsonSerializer.Serialize(body, JsonOpts);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                summary.Error = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                summary.DurationMs = sw.ElapsedMilliseconds;
                return (summary, null);
            }
            var parsed = JsonSerializer.Deserialize<BowireParallelResponse>(
                responseBody, BowireEndpointHelpers.JsonOptions);
            if (parsed is null)
            {
                summary.Error = "Empty response body";
                summary.DurationMs = sw.ElapsedMilliseconds;
                return (summary, null);
            }
            summary.PassCount = parsed.PassCount;
            summary.FailCount = parsed.FailCount;
            summary.DurationMs = sw.ElapsedMilliseconds;
            return (summary, parsed);
        }
#pragma warning disable CA1031 // Per-host failure must not abort the fan-out — surface it on the per-host summary.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            sw.Stop();
            logger.LogWarning(ex, "Parallel coordinator host {Host} call failed", host);
            summary.Error = ex.Message;
            summary.DurationMs = sw.ElapsedMilliseconds;
            return (summary, null);
        }
    }
}

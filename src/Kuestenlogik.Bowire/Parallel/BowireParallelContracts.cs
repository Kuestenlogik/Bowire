// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Parallel;

/// <summary>
/// Wire contracts for the #132 Phase 2 parallel-sessions surface.
/// <para>
/// A <see cref="BowireParallelTarget"/> is one self-contained
/// upstream call — the JS layer pre-resolves the recording / collection
/// item into a flat sequence of these so the worker stays oblivious to
/// the source shape. Each session walks its assigned slice
/// sequentially; sessions run concurrently.
/// </para>
/// <para>
/// The coordinator (<c>POST /api/parallel/start</c>) takes the same
/// shape as the per-host worker (<c>POST /api/parallel/start-local</c>)
/// plus a list of host URLs. It shards the requested session count
/// across those hosts and POSTs to each <c>/api/parallel/start-local</c>
/// in parallel, then merges the per-host results into the response.
/// </para>
/// </summary>
internal static class BowireParallelContracts
{
    // Marker class — the actual records live below. Kept as the public
    // anchor for the file so the namespace's purpose stays grep-able.
}

/// <summary>One upstream call the worker can replay.</summary>
internal sealed class BowireParallelTarget
{
    /// <summary>Absolute upstream URL. The worker POSTs to this URL directly.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HTTP method. Defaults to POST.</summary>
    public string Method { get; set; } = "POST";

    /// <summary>Request body (JSON or whatever the upstream accepts).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Request headers — applied verbatim. Authorization typically lives here.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Optional label used in the per-target result row (defaults to <c>method + path</c>).</summary>
    public string? Label { get; set; }

    /// <summary>Optional protocol tag — passed through for the UI's column.</summary>
    public string? Protocol { get; set; }
}

/// <summary>Body for <c>POST /api/parallel/start-local</c> (per-host worker).</summary>
internal class BowireParallelLocalRequest
{
    /// <summary>The full target list — sessions walk a round-robin slice each.</summary>
    public List<BowireParallelTarget> Targets { get; set; } = [];

    /// <summary>How many concurrent sessions to launch in-process.</summary>
    public int SessionCount { get; set; } = 1;

    /// <summary>
    /// Stagger window for session starts. <c>0</c> = launch all sessions
    /// immediately. <c>N &gt; 0</c> = spread starts evenly over N seconds.
    /// </summary>
    public double RampUpSeconds { get; set; }

    /// <summary>
    /// When <c>true</c>, a session keeps running after a target fails.
    /// When <c>false</c>, the first failure cancels all in-flight
    /// sessions and the run terminates with the partial result set.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Optional per-session environment pool. Each session is assigned
    /// one env id via round-robin (session k → pool[k % pool.Count]).
    /// The env id surfaces in the per-session result so the operator
    /// can attribute failures back to a specific env slot, and is
    /// echoed as the <c>X-Bowire-Env</c> header on every upstream call
    /// so an upstream that splits state per env stays honest. Empty
    /// pool = every session reuses the active env (no header sent).
    /// </summary>
    public List<string>? EnvPool { get; set; }
}

/// <summary>Body for <c>POST /api/parallel/start</c> (coordinator).</summary>
internal sealed class BowireParallelDistributedRequest : BowireParallelLocalRequest
{
    /// <summary>
    /// Remote Bowire hosts to fan out to. Each entry is the host's
    /// base URL (e.g. <c>https://exec-eu.example:5080</c>). The
    /// coordinator POSTs <c>{baseUrl}/api/parallel/start-local</c> on
    /// every host with the same target list and a session-count
    /// shard. When empty, the coordinator runs everything in-process
    /// (equivalent to calling <c>/start-local</c> directly).
    /// </summary>
    public List<string>? Hosts { get; set; }

    /// <summary>
    /// Optional bearer token forwarded as <c>Authorization: Bearer
    /// {Token}</c> on the outbound calls to peer hosts. When
    /// <c>null</c>, the coordinator falls back to the
    /// <c>BOWIRE_PARALLEL_TOKEN</c> environment variable, then to
    /// no auth. Keeps the token off the wire from the browser —
    /// the JS layer only sees the host list, the secret stays in
    /// the coordinator process.
    /// </summary>
    public string? Token { get; set; }
}

/// <summary>Per-target outcome reported back to the coordinator.</summary>
internal sealed class BowireParallelTargetResult
{
    public int SessionIndex { get; set; }
    public int TargetIndex { get; set; }
    public string? Label { get; set; }
    public bool Pass { get; set; }
    public int Status { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>Per-session summary reported back to the coordinator.</summary>
internal sealed class BowireParallelSessionResult
{
    public int SessionIndex { get; set; }
    public string? EnvId { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public long DurationMs { get; set; }
    public string? Aborted { get; set; }
}

/// <summary>Response shape for <c>/start-local</c> and <c>/start</c>.</summary>
internal sealed class BowireParallelResponse
{
    public int SessionCount { get; set; }
    public int TargetCount { get; set; }
    public long TotalDurationMs { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }

    /// <summary>Per-session summary, ordered by session index.</summary>
    public List<BowireParallelSessionResult> Sessions { get; set; } = [];

    /// <summary>Flat list of per-target outcomes across every session.</summary>
    public List<BowireParallelTargetResult> Results { get; set; } = [];

    /// <summary>
    /// Per-host summary on a coordinator response. Empty / absent on
    /// a per-host worker response.
    /// </summary>
    [JsonPropertyName("hosts")]
    public List<BowireParallelHostSummary>? Hosts { get; set; }
}

/// <summary>Per-host roll-up on the coordinator response.</summary>
internal sealed class BowireParallelHostSummary
{
    public string Host { get; set; } = string.Empty;
    public int SessionCount { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}

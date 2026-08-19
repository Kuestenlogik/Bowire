// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>What to measure and how hard to push (#360).</summary>
public sealed class BowireBenchmarkRequest
{
    /// <summary>Target server URL.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Service to call.</summary>
    public required string Service { get; init; }

    /// <summary>Method to call.</summary>
    public required string Method { get; init; }

    /// <summary>Request payload, if the method takes one.</summary>
    public string? Body { get; init; }

    /// <summary>Total number of calls to make.</summary>
    public int Iterations { get; init; } = 50;

    /// <summary>How many calls may be in flight at once.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Calls made before measurement starts, to shake out JIT / connection setup.</summary>
    public int Warmup { get; init; }

    /// <summary>Metadata headers to send with every call.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>One completed benchmark run.</summary>
/// <param name="Stats">Aggregate statistics over the measured calls.</param>
/// <param name="ElapsedMs">Wall-clock duration of the measured phase.</param>
/// <param name="FirstError">The first error message observed, if any — the operator's first clue.</param>
public sealed record BowireBenchmarkRun(BowireBenchmarkStats Stats, long ElapsedMs, string? FirstError);

/// <summary>
/// Drives a protocol plugin's invoke path in a loop and collects latencies
/// (#360). Deliberately thin: it owns concurrency, warm-up and timing, and
/// leaves the transport to <see cref="IBowireProtocol"/> — the same seam
/// <c>bowire call</c> and <c>bowire test</c> invoke through, so a benchmark
/// measures the request path the rest of the tool actually uses.
/// </summary>
public static class BowireBenchmarkRunner
{
    /// <summary>
    /// Run <paramref name="request"/> against <paramref name="protocol"/>.
    /// Failures are counted rather than thrown: a benchmark's job is to
    /// report an error rate, not to abandon the run on the first 500.
    /// </summary>
    public static async Task<BowireBenchmarkRun> RunAsync(
        IBowireProtocol protocol, BowireBenchmarkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(request);

        var iterations = Math.Max(0, request.Iterations);
        var concurrency = Math.Max(1, request.Concurrency);

        // Warm-up calls are made and discarded — their latencies would
        // otherwise drag the percentiles toward first-call costs (JIT,
        // TLS handshake, connection pool fill) that no steady-state
        // consumer pays.
        for (var i = 0; i < Math.Max(0, request.Warmup); i++)
        {
            ct.ThrowIfCancellationRequested();
            await InvokeOnceAsync(protocol, request, ct).ConfigureAwait(false);
        }

        var latencies = new double[iterations];
        var failed = new bool[iterations];
        var errors = new string?[iterations];

        var sw = Stopwatch.StartNew();
        if (iterations > 0)
        {
            // A bounded worker pool rather than one task per iteration:
            // 10_000 iterations at concurrency 4 must be four callers
            // taking the next index, not 10_000 tasks fighting the pool.
            var next = -1;
            var workers = new Task[Math.Min(concurrency, iterations)];
            for (var w = 0; w < workers.Length; w++)
            {
                workers[w] = Task.Run(async () =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next);
                        if (index >= iterations) return;
                        ct.ThrowIfCancellationRequested();

                        var callSw = Stopwatch.StartNew();
                        var (ok, error) = await InvokeOnceAsync(protocol, request, ct).ConfigureAwait(false);
                        callSw.Stop();

                        latencies[index] = callSw.Elapsed.TotalMilliseconds;
                        failed[index] = !ok;
                        errors[index] = error;
                    }
                }, ct);
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        sw.Stop();

        // A failed call's duration is not a latency sample — it usually
        // measures how fast the server said no. Count it in the error rate
        // and keep it out of the percentiles.
        var measured = new List<double>(iterations);
        var errorCount = 0;
        string? firstError = null;
        for (var i = 0; i < iterations; i++)
        {
            if (failed[i])
            {
                errorCount++;
                firstError ??= errors[i];
            }
            else
            {
                measured.Add(latencies[i]);
            }
        }

        var stats = BowireBenchmarkStats.From(measured, errorCount, sw.Elapsed.TotalSeconds);
        return new BowireBenchmarkRun(stats, sw.ElapsedMilliseconds, firstError);
    }

    private static async Task<(bool Ok, string? Error)> InvokeOnceAsync(
        IBowireProtocol protocol, BowireBenchmarkRequest request, CancellationToken ct)
    {
        try
        {
            var metadata = request.Metadata is null
                ? null
                : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);
            var messages = request.Body is null ? new List<string>() : [request.Body];

            var result = await protocol.InvokeAsync(
                request.ServerUrl, request.Service, request.Method,
                messages, false, metadata, ct).ConfigureAwait(false);

            return IsOk(result?.Status)
                ? (true, null)
                : (false, $"status {result?.Status ?? "(none)"}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any transport-level throw is one failed call, not a failed
            // run — the whole point of the error-rate metric.
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Whether a call counts as successful. Mirrors <c>isHistoryEntryOk</c>
    /// in the workbench (history-env.js) exactly — 'OK' / 'Connected' /
    /// 'Completed', any numeric status below 400, everything else an error.
    /// The UI's benchmark rail classifies through that same helper, and a
    /// CLI that drew the line elsewhere would report a different error rate
    /// than the rail for an identical run.
    /// </summary>
    public static bool IsOk(string? status)
    {
        if (string.IsNullOrEmpty(status)) return true;
        if (status is "OK" or "Connected" or "Completed") return true;
        if (int.TryParse(status, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var code))
        {
            return code < 400;
        }
        // A gRPC status name other than OK is an error.
        return false;
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Benchmarking;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// #232 — the scheduler pass. Driven through <c>TickAsync</c> with an
/// explicit clock so due-ness, the no-double-fire rule and the recorded
/// result are asserted without waiting on wall-clock timers.
/// </summary>
public sealed class BowireBenchmarkSchedulingHostedServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BowireBenchmarkScheduleStore _store;
    private readonly StubResolver _resolver = new();
    // One instance for the fixture: BackgroundService is IDisposable, and the
    // scheduler keeps no state between ticks (it re-reads the store), so a
    // per-call factory would only leak handles (CA2000).
    private readonly BowireBenchmarkSchedulingHostedService _service;

    public BowireBenchmarkSchedulingHostedServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bowire-schedsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new BowireBenchmarkScheduleStore(_root);
        _service = new BowireBenchmarkSchedulingHostedService(
            _store, _resolver, NullLogger<BowireBenchmarkSchedulingHostedService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static BowireBenchmarkSchedule EveryMinute(string id = "s1", bool enabled = true)
        => new()
        {
            Id = id, Name = id, Cron = "* * * * *", Enabled = enabled,
            ServerUrl = "http://h", Protocol = "stub", Service = "Svc", Method = "M",
            Iterations = 3,
        };

    [Fact]
    public async Task Tick_FiresADueScheduleAndRecordsTheRun()
    {
        var schedule = EveryMinute();
        await _store.SaveAsync(schedule, TestContext.Current.CancellationToken);

        // First tick establishes the reference point (a new schedule must not
        // fire for every occurrence since the epoch).
        var now = new DateTime(2026, 5, 1, 12, 0, 30, DateTimeKind.Utc);
        Assert.Equal(0, await _service.TickAsync(now, TestContext.Current.CancellationToken));

        // …so nothing has run yet; drive a manual run to plant the reference,
        // then a later tick must fire.
        await _service.RunAsync(schedule, now, "manual", TestContext.Current.CancellationToken);
        var fired = await _service.TickAsync(now.AddMinutes(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, fired);
        var runs = await _store.LoadRunsAsync("s1", TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.Equal("schedule", runs[0].TriggeredBy);   // newest first
        Assert.Equal("manual", runs[1].TriggeredBy);
        Assert.Equal(3, runs[0].Count);
    }

    [Fact]
    public async Task Tick_DoesNotFireTwiceForTheSameOccurrence()
    {
        var schedule = EveryMinute();
        await _store.SaveAsync(schedule, TestContext.Current.CancellationToken);
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        await _service.RunAsync(schedule, now, "manual", TestContext.Current.CancellationToken);

        // Two ticks at the same instant: the second sees the run the first
        // recorded and stays quiet.
        var first = await _service.TickAsync(now.AddMinutes(2), TestContext.Current.CancellationToken);
        var second = await _service.TickAsync(now.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Tick_SkipsAPausedSchedule()
    {
        var schedule = EveryMinute(enabled: false);
        await _store.SaveAsync(schedule, TestContext.Current.CancellationToken);
        await _store.AppendRunAsync(new BowireBenchmarkScheduleRun
        { ScheduleId = "s1", StartedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) },
            TestContext.Current.CancellationToken);

        var fired = await _service.TickAsync(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, fired);
        Assert.Equal(0, _resolver.Protocol.Calls);
    }

    [Fact]
    public async Task Tick_SurvivesAScheduleWithABadCronExpression()
    {
        // One unusable entry must not stop the healthy one beside it.
        var broken = EveryMinute("broken");
        broken.Cron = "not a cron";
        await _store.SaveAsync(broken, TestContext.Current.CancellationToken);

        var healthy = EveryMinute("healthy");
        await _store.SaveAsync(healthy, TestContext.Current.CancellationToken);
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        await _service.RunAsync(healthy, now, "manual", TestContext.Current.CancellationToken);

        var fired = await _service.TickAsync(now.AddMinutes(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Run_RecordsThresholdVerdictsAndTheOverallPass()
    {
        var schedule = EveryMinute();
        schedule.Thresholds.Add("p95 < 10000");   // holds
        schedule.Thresholds.Add("p95 < 0.0001");  // breached
        await _store.SaveAsync(schedule, TestContext.Current.CancellationToken);

        var run = await _service.RunAsync(schedule, DateTime.UtcNow, "manual", TestContext.Current.CancellationToken);

        Assert.NotNull(run);
        Assert.Equal(2, run!.Thresholds.Count);
        Assert.False(run.Passed);
        Assert.Contains(run.Thresholds, t => t.Ok);
        Assert.Contains(run.Thresholds, t => !t.Ok);
    }

    [Fact]
    public async Task Run_UnparseableThresholdIsSkippedNotFatal()
    {
        var schedule = EveryMinute();
        schedule.Thresholds.Add("nonsense");
        await _store.SaveAsync(schedule, TestContext.Current.CancellationToken);

        var run = await _service.RunAsync(schedule, DateTime.UtcNow, "manual", TestContext.Current.CancellationToken);

        Assert.NotNull(run);
        Assert.Empty(run!.Thresholds);
        Assert.True(run.Passed);
    }

    [Fact]
    public async Task Run_UnknownProtocolIsReportedNotThrown()
    {
        var schedule = EveryMinute();
        schedule.Protocol = "does-not-exist";

        var run = await _service.RunAsync(schedule, DateTime.UtcNow, "manual", TestContext.Current.CancellationToken);

        Assert.Null(run);
        Assert.Empty(await _store.LoadRunsAsync("s1", TestContext.Current.CancellationToken));
    }

    private sealed class StubResolver : IBowireBenchmarkProtocolResolver
    {
        public CountingProtocol Protocol { get; } = new();

        public IBowireProtocol? Resolve(string protocolId)
            => string.Equals(protocolId, "stub", StringComparison.OrdinalIgnoreCase) ? Protocol : null;
    }

    private sealed class CountingProtocol : IBowireProtocol
    {
        private int _calls;
        public int Calls => _calls;

        public string Id => "stub";
        public string Name => "stub";
        public string IconSvg => "";

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new InvokeResult("{}", 1, "OK", []));
        }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}

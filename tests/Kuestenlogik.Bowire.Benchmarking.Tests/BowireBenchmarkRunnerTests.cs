// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Benchmarking;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// #360 — the invoke loop. Driven through a scripted <see cref="IBowireProtocol"/>
/// so iteration counting, concurrency, warm-up and the success/failure rule
/// are asserted without a live server.
/// </summary>
public sealed class BowireBenchmarkRunnerTests
{
    private static BowireBenchmarkRequest Request(int iterations = 10, int concurrency = 1, int warmup = 0)
        => new()
        {
            ServerUrl = "http://localhost",
            Service = "Svc",
            Method = "M",
            Iterations = iterations,
            Concurrency = concurrency,
            Warmup = warmup,
        };

    [Fact]
    public async Task RunAsync_MakesExactlyTheRequestedNumberOfCalls()
    {
        var protocol = new ScriptedProtocol();
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 25),
            TestContext.Current.CancellationToken);

        Assert.Equal(25, protocol.Calls);
        Assert.Equal(25, run.Stats.Count);
        Assert.Equal(0, run.Stats.Errors);
    }

    [Fact]
    public async Task RunAsync_WarmupCallsAreMadeButNotMeasured()
    {
        var protocol = new ScriptedProtocol();
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 10, warmup: 5),
            TestContext.Current.CancellationToken);

        Assert.Equal(15, protocol.Calls);      // warm-up really ran
        Assert.Equal(10, run.Stats.Count);     // but stayed out of the sample
    }

    [Fact]
    public async Task RunAsync_HonoursConcurrencyWithoutLosingIterations()
    {
        // The bounded worker pool must still make exactly N calls, and must
        // actually overlap them — MaxInFlight proves the second part.
        var protocol = new ScriptedProtocol { DelayMs = 15 };
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 20, concurrency: 4),
            TestContext.Current.CancellationToken);

        Assert.Equal(20, protocol.Calls);
        Assert.Equal(20, run.Stats.Count);
        Assert.True(protocol.MaxInFlight > 1, $"expected overlap, saw {protocol.MaxInFlight}");
        Assert.True(protocol.MaxInFlight <= 4, $"exceeded the concurrency cap: {protocol.MaxInFlight}");
    }

    [Fact]
    public async Task RunAsync_ConcurrencyAboveIterationsDoesNotOverRun()
    {
        var protocol = new ScriptedProtocol();
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 3, concurrency: 16),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, protocol.Calls);
        Assert.Equal(3, run.Stats.Count);
    }

    [Fact]
    public async Task RunAsync_ErrorStatusesCountAsFailuresNotLatencies()
    {
        // Every third call answers 500.
        var protocol = new ScriptedProtocol { StatusFor = i => i % 3 == 0 ? "500" : "OK" };
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 9),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, run.Stats.Errors);
        Assert.Equal(6, run.Stats.Count);
        Assert.Equal(3.0 / 9, run.Stats.ErrorRate, 6);
        Assert.NotNull(run.FirstError);
    }

    [Fact]
    public async Task RunAsync_AThrowingTransportIsOneFailedCallNotAFailedRun()
    {
        var protocol = new ScriptedProtocol { ThrowEvery = 2 };
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 6),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, run.Stats.Errors);
        Assert.Equal(3, run.Stats.Count);
        Assert.Contains("boom", run.FirstError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ZeroIterationsIsAnEmptyRunNotACrash()
    {
        var protocol = new ScriptedProtocol();
        var run = await BowireBenchmarkRunner.RunAsync(protocol, Request(iterations: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, protocol.Calls);
        Assert.Equal(0, run.Stats.Count);
        Assert.Null(run.FirstError);
    }

    [Theory]
    [InlineData("OK", true)]
    [InlineData("Connected", true)]
    [InlineData("Completed", true)]
    [InlineData("200", true)]
    [InlineData("301", true)]
    [InlineData("404", false)]
    [InlineData("500", false)]
    [InlineData("NotFound", false)]     // gRPC status name
    [InlineData("", true)]              // no status reported → not an error
    [InlineData(null, true)]
    public void IsOk_MatchesTheWorkbenchsRule(string? status, bool expected)
        => Assert.Equal(expected, BowireBenchmarkRunner.IsOk(status));

    /// <summary>
    /// Minimal protocol stub: counts calls, can delay, and can script a
    /// status or a throw per call index.
    /// </summary>
    private sealed class ScriptedProtocol : IBowireProtocol
    {
        private int _calls;
        private int _inFlight;
        private int _maxInFlight;

        public int Calls => _calls;
        public int MaxInFlight => _maxInFlight;
        public int DelayMs { get; init; }
        public int ThrowEvery { get; init; }
        public Func<int, string>? StatusFor { get; init; }

        public string Id => "scripted";
        public string Name => "scripted";
        public string IconSvg => "";

        public async Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _inFlight);
            // Racy by nature; a high-water mark only ever under-reports, so
            // the "> 1" assertion stays sound.
            if (now > _maxInFlight) _maxInFlight = now;
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
                if (ThrowEvery > 0 && index % ThrowEvery == 0) throw new InvalidOperationException("boom");
                var status = StatusFor?.Invoke(index) ?? "OK";
                return new InvokeResult("{}", 1, status, []);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
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
